using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Courier.Core.Abstractions;
using Courier.Core.Storage;

namespace Courier.Platform.Windows.Secrets;

/// <summary>
/// Windows Credential Manager, reached by P/Invoke. STOR-04.
/// </summary>
/// <remarks>
/// <para>
/// TECH_SPEC 3.3: no third-party cross-platform keyring wrapper. This is a small amount of
/// P/Invoke, it is the single most security-sensitive component in the product, and a dependency
/// here would mean trusting someone else's marshalling with every credential the user owns.
/// </para>
/// <para>
/// The credential blob has a 2560-byte limit, which a long access token can exceed. Anything over
/// the limit is encrypted with DPAPI under the current user and written to a file, with the
/// credential entry holding a pointer to it. The plaintext never touches disk either way.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecretStore : ISecretStore
{
    private const string TargetPrefix = "Courier:";
    private const int MaxCredentialBlobBytes = 2560;
    private const string OverflowMarker = "courier-dpapi:";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string LocationDescription => "Windows Credential Manager (Generic credentials, prefix 'Courier:')";

    public bool IsHardwareBacked => false;

    public ValueTask<string?> GetAsync(SecretKey key, CancellationToken ct = default)
    {
        var target = TargetPrefix + key;

        if (!CredReadW(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound
                ? ValueTask.FromResult<string?>(null)
                : throw new SecretStoreException($"Could not read '{key}' from Credential Manager.", error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIALW>(handle);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return ValueTask.FromResult<string?>(null);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.UTF8.GetString(bytes);

            return ValueTask.FromResult<string?>(
                value.StartsWith(OverflowMarker, StringComparison.Ordinal)
                    ? ReadOverflow(value[OverflowMarker.Length..])
                    : value);
        }
        finally
        {
            CredFree(handle);
        }
    }

    public ValueTask SetAsync(SecretKey key, string value, CancellationToken ct = default)
    {
        var target = TargetPrefix + key;
        var bytes = Encoding.UTF8.GetBytes(value);

        if (bytes.Length > MaxCredentialBlobBytes)
        {
            var pointer = WriteOverflow(key, value);
            bytes = Encoding.UTF8.GetBytes(OverflowMarker + pointer);
        }

        var blob = Marshal.AllocHGlobal(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new CREDENTIALW
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = Environment.UserName,
                Comment = "Stored by Courier. Deleting this removes the value from Courier too.",
            };

            if (!CredWriteW(ref credential, 0))
            {
                throw new SecretStoreException(
                    $"Could not write '{key}' to Credential Manager.",
                    Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            // Zero before freeing: the plaintext was in unmanaged memory for the duration of the
            // call and there is no reason to leave it there.
            for (var i = 0; i < bytes.Length; i++)
            {
                Marshal.WriteByte(blob, i, 0);
            }

            Marshal.FreeHGlobal(blob);
            Array.Clear(bytes);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DeleteAsync(SecretKey key, CancellationToken ct = default)
    {
        var overflowPath = OverflowPath(key);
        if (File.Exists(overflowPath))
        {
            File.Delete(overflowPath);
        }

        if (!CredDeleteW(TargetPrefix + key, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new SecretStoreException($"Could not delete '{key}' from Credential Manager.", error);
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SecretKey>> ListAsync(string scope, CancellationToken ct = default)
    {
        var filter = $"{TargetPrefix}{scope}/*";

        if (!CredEnumerateW(filter, 0, out var count, out var credentials))
        {
            // No matches enumerates as a failure with ERROR_NOT_FOUND. Not an error condition.
            return ValueTask.FromResult<IReadOnlyList<SecretKey>>([]);
        }

        try
        {
            var keys = new List<SecretKey>((int)count);

            for (var i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(credentials, i * IntPtr.Size);
                var credential = Marshal.PtrToStructure<CREDENTIALW>(pointer);
                var name = credential.TargetName;

                if (name?.StartsWith(TargetPrefix, StringComparison.Ordinal) != true)
                {
                    continue;
                }

                var identifier = name[TargetPrefix.Length..];
                var slash = identifier.IndexOf('/');

                if (slash > 0)
                {
                    keys.Add(new SecretKey(identifier[..slash], identifier[(slash + 1)..]));
                }
            }

            return ValueTask.FromResult<IReadOnlyList<SecretKey>>(keys);
        }
        finally
        {
            CredFree(credentials);
        }
    }

    /// <summary>
    /// DPAPI under the current user, for values the credential blob cannot hold. The file is opaque
    /// to every other account on the machine and useless if copied off it.
    /// </summary>
    private static string WriteOverflow(SecretKey key, string value)
    {
        var directory = Path.Combine(StorageLocations.Root, "protected");
        Directory.CreateDirectory(directory);

        var path = OverflowPath(key);
        var plaintext = Encoding.UTF8.GetBytes(value);

        try
        {
            var protectedBytes = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
        }
        finally
        {
            Array.Clear(plaintext);
        }

        return Path.GetFileName(path);
    }

    private static string? ReadOverflow(string fileName)
    {
        var path = Path.Combine(StorageLocations.Root, "protected", fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        var plaintext = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);

        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    private static string OverflowPath(SecretKey key)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())));
        return Path.Combine(StorageLocations.Root, "protected", $"{hash[..32]}.bin");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref CREDENTIALW credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerateW(string filter, int flags, out uint count, out IntPtr credentials);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}

public sealed class SecretStoreException(string message, int win32Error)
    : Exception($"{message} (Windows error {win32Error}.)")
{
    public int Win32Error { get; } = win32Error;
}
