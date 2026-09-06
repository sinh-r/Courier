using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Courier.Core.Abstractions;
using Courier.Core.Storage;

namespace Courier.Platform.Posix;

/// <summary>
/// Best-effort fallbacks so a contributor on macOS or Linux can build and run. TECH_SPEC 1.3.
/// </summary>
/// <remarks>
/// These are honest about what they cannot do. A fallback that pretends to be as strong as the
/// Windows implementation is worse than one that says it is not — the whole product rests on the
/// user knowing where their secrets are.
/// </remarks>
public sealed class FallbackSecretStore : ISecretStore
{
    private readonly string _path = Path.Combine(StorageLocations.Root, "secrets.fallback.json");
    private readonly Lock _lock = new();

    /// <summary>Stated verbatim on the storage settings page, where it should be alarming.</summary>
    public string LocationDescription =>
        OperatingSystem.IsMacOS()
            ? "a local file encrypted with a machine key. The macOS Keychain is not yet used; do not store production credentials here."
            : "a local file encrypted with a machine key. No OS credential store is in use; do not store production credentials here.";

    public bool IsHardwareBacked => false;

    public ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var all = Read();
            return ValueTask.FromResult(
                all.TryGetValue(key.ToString(), out var encrypted) ? Decrypt(encrypted) : null);
        }
    }

    public ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var all = Read();
            all[key.ToString()] = Encrypt(value);
            Write(all);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var all = Read();
            if (all.Remove(key.ToString()))
            {
                Write(all);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var prefix = scope + "/";
            var keys = Read().Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => new SecretKey(scope, k[prefix.Length..]))
                .ToList();

            return ValueTask.FromResult<IReadOnlyList<SecretKey>>(keys);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? [];
    }

    private void Write(Dictionary<string, string> all)
    {
        StorageLocations.EnsureCreated();
        File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// AES-GCM under a key derived from the machine and user identity. This stops a casual read of
    /// the file, and nothing more; it is not equivalent to a credential store and does not claim to be.
    /// </summary>
    private static string Encrypt(string value)
    {
        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        Array.Clear(plaintext);

        return $"{Convert.ToBase64String(nonce)}.{Convert.ToBase64String(tag)}.{Convert.ToBase64String(ciphertext)}";
    }

    private static string? Decrypt(string stored)
    {
        var parts = stored.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var key = DeriveKey();
            var nonce = Convert.FromBase64String(parts[0]);
            var tag = Convert.FromBase64String(parts[1]);
            var ciphertext = Convert.FromBase64String(parts[2]);
            var plaintext = new byte[ciphertext.Length];

            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private static byte[] DeriveKey()
    {
        var material = $"{Environment.MachineName}|{Environment.UserName}|courier-fallback";
        return SHA256.HashData(Encoding.UTF8.GetBytes(material));
    }
}

/// <summary>Environment-variable proxy configuration, the POSIX convention.</summary>
public sealed class EnvironmentProxyResolver : IProxyResolver
{
    public ValueTask<ProxyDecision> ResolveAsync(Uri destination, CancellationToken ct = default)
    {
        var noProxy = Environment.GetEnvironmentVariable("no_proxy")
            ?? Environment.GetEnvironmentVariable("NO_PROXY");

        if (noProxy is not null && noProxy
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(entry => destination.Host.EndsWith(entry.TrimStart('*'), StringComparison.OrdinalIgnoreCase)))
        {
            return ValueTask.FromResult(ProxyDecision.Direct("no_proxy"));
        }

        var variable = destination.Scheme == "https" ? "https_proxy" : "http_proxy";
        var value = Environment.GetEnvironmentVariable(variable)
            ?? Environment.GetEnvironmentVariable(variable.ToUpperInvariant());

        return ValueTask.FromResult(
            Uri.TryCreate(value, UriKind.Absolute, out var proxy)
                ? new ProxyDecision(proxy, $"from ${variable}")
                : ProxyDecision.Direct("no proxy environment variable"));
    }

    public ValueTask<SystemProxyDescription> DescribeSystemProxyAsync(CancellationToken ct = default)
    {
        var https = Environment.GetEnvironmentVariable("https_proxy")
            ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");

        var bypass = (Environment.GetEnvironmentVariable("no_proxy")
            ?? Environment.GetEnvironmentVariable("NO_PROXY")
            ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Uri.TryCreate(https, UriKind.Absolute, out var proxy);
        return ValueTask.FromResult(new SystemProxyDescription(proxy, null, bypass, "from environment variables"));
    }
}

/// <summary>OpenSSL's own verdict, with the same per-host exception model. ENT-11.</summary>
public sealed class FallbackTrustStore : ITrustStore
{
    private readonly Dictionary<string, HostTrustException> _exceptions = new(StringComparer.OrdinalIgnoreCase);

    public TrustDecision Validate(string host, X509Certificate2? certificate, X509Chain? chain, bool platformSaysValid)
    {
        if (platformSaysValid)
        {
            return TrustDecision.Trusted("from the system trust store");
        }

        var problems = chain?.ChainStatus
            .Where(s => s.Status != X509ChainStatusFlags.NoError)
            .Select(s => new ChainElementProblem(
                certificate?.Subject ?? host,
                certificate?.Thumbprint ?? string.Empty,
                s.Status.ToString(),
                s.StatusInformation.Trim()))
            .ToList() ?? [];

        if (certificate is not null
            && _exceptions.TryGetValue(host, out var recorded)
            && string.Equals(recorded.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            return new TrustDecision(true, "allowed for this host only · set by you", problems);
        }

        return new TrustDecision(false, "not trusted by the system trust store", problems);
    }

    public ValueTask<IReadOnlyList<HostTrustException>> ListExceptionsAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<HostTrustException>>([.. _exceptions.Values]);

    public ValueTask AllowHostAsync(string host, string thumbprint, string reason, CancellationToken ct = default)
    {
        _exceptions[host] = new HostTrustException(host, thumbprint, reason, DateTimeOffset.UtcNow);
        return ValueTask.CompletedTask;
    }

    public ValueTask RevokeHostAsync(string host, CancellationToken ct = default)
    {
        _exceptions.Remove(host);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Certificates from a .pfx file only. There is no OS store to enumerate here.</summary>
public sealed class FileCertificateSource : ICertificateSource
{
    private readonly Dictionary<string, X509Certificate2> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<CertificateCandidate>> ListAsync(CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<CertificateCandidate>>(
        [
            .. _loaded.Values.Select(c => new CertificateCandidate(
                c.Subject,
                c.Thumbprint,
                c.NotBefore,
                c.NotAfter,
                "from a .pfx file you chose",
                IsHardwareBacked: false)),
        ]);

    public ValueTask<X509Certificate2?> ResolveAsync(string thumbprint, CancellationToken ct = default) =>
        ValueTask.FromResult(_loaded.GetValueOrDefault(thumbprint));

    public ValueTask<X509Certificate2> LoadFromFileAsync(string path, string? password, CancellationToken ct = default)
    {
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password, X509KeyStorageFlags.EphemeralKeySet);
        _loaded[certificate.Thumbprint] = certificate;
        return ValueTask.FromResult(certificate);
    }
}

/// <summary>Integrated Windows auth is unavailable here, and says so rather than failing obscurely.</summary>
public sealed class UnavailableIntegratedAuth : IIntegratedAuthProvider
{
    public bool IsAvailable => false;

    public string? CurrentIdentity => null;

    public ICredentials? GetDefaultCredentials() => null;
}

/// <summary>Opens the containing folder with the desktop's own file browser. SEC-05.</summary>
public sealed class PosixFileRevealer : IFileRevealer
{
    public bool CanReveal => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public void RevealInFileBrowser(string path)
    {
        if (!CanReveal || (!File.Exists(path) && !Directory.Exists(path)))
        {
            return;
        }

        var (command, arguments) = OperatingSystem.IsMacOS()
            ? ("open", File.Exists(path) ? $"-R \"{path}\"" : $"\"{path}\"")
            : ("xdg-open", $"\"{(File.Exists(path) ? Path.GetDirectoryName(path) : path)}\"");

        Process.Start(new ProcessStartInfo(command, arguments) { UseShellExecute = false });
    }
}

/// <summary>No file association mechanism is assumed here. CAP-05 degrades to drag-drop and open.</summary>
public sealed class NoFileAssociationRegistrar : IFileAssociationRegistrar
{
    public bool IsSupported => false;

    public bool IsRegistered(string extension) => false;

    public void Register(string extension, string description, string executablePath)
    {
    }

    public void Unregister(string extension)
    {
    }
}

/// <summary>Never prompts through a broker, so there is no window to parent to.</summary>
public sealed class NoBrokerWindowHandleProvider : IBrokerWindowHandleProvider
{
    public IntPtr GetActiveWindowHandle() => IntPtr.Zero;
}
