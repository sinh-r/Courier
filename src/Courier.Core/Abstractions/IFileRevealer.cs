namespace Courier.Core.Abstractions;

/// <summary>
/// One-click reveal in the OS file browser, for the storage settings page. SEC-05.
/// </summary>
public interface IFileRevealer
{
    bool CanReveal { get; }

    void RevealInFileBrowser(string path);
}

/// <summary>
/// Registers the capsule file association so a double-clicked capsule opens in Courier. CAP-05.
/// </summary>
public interface IFileAssociationRegistrar
{
    bool IsSupported { get; }

    bool IsRegistered(string extension);

    /// <summary>Per-user registration only. Never requires admin rights (NFR-02).</summary>
    void Register(string extension, string description, string executablePath);

    void Unregister(string extension);
}
