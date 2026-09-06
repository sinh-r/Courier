using System.Diagnostics;
using System.Runtime.Versioning;
using Courier.Core.Abstractions;
using Microsoft.Win32;

namespace Courier.Platform.Windows.Shell;

/// <summary>One-click reveal in Explorer, for the storage settings page. SEC-05.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileRevealer : IFileRevealer
{
    public bool CanReveal => true;

    public void RevealInFileBrowser(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        // /select, highlights the item; without it Explorer opens the file itself, which for a
        // .db would launch whatever is registered for it.
        var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}

/// <summary>
/// Registers the capsule file association so a double-clicked capsule opens in Courier. CAP-05.
/// </summary>
/// <remarks>
/// Per-user under HKCU only. NFR-02 requires a portable mode needing no installer and no admin
/// rights, and writing to HKLM would break that on exactly the locked-down machines Courier is
/// meant to slip into.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileAssociationRegistrar : IFileAssociationRegistrar
{
    private const string ProgId = "Courier.Capsule";

    public bool IsSupported => true;

    public bool IsRegistered(string extension)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}");
        return key?.GetValue(null) as string == ProgId;
    }

    public void Register(string extension, string description, string executablePath)
    {
        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue(null, description);

            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{executablePath}\",0");

            using var command = progId.CreateSubKey(@"shell\open\command");

            // The capsule is passed as an argument and opened as inert data. CAP-09: nothing in it
            // is executed, and the command line gives it no opportunity to be.
            command.SetValue(null, $"\"{executablePath}\" --open-capsule \"%1\"");
        }

        using var association = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{extension}");
        association.SetValue(null, ProgId);
        association.SetValue("PerceivedType", "document");

        using var openWith = association.CreateSubKey(@"OpenWithProgids");
        openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    public void Unregister(string extension)
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{extension}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
    }
}
