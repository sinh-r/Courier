using Courier.Core.Privacy;
using Courier.Core.Storage;

namespace Courier.App.Services;

/// <summary>
/// Writes a crash or a deferred-job failure to a local file. Nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// SEC-01: there is no crash reporting service, opt-in or otherwise. A stack trace goes to
/// <c>%LOCALAPPDATA%\Courier\logs</c> and stays there, and the storage settings page lists it so
/// the user can read it, reveal it or delete it.
/// </para>
/// <para>
/// SEC-07: everything written passes through the same scrubbing as the ordinary log. An exception
/// message is a common way for a token to end up in a file — <c>HttpRequestException</c> quoting a
/// URL with a SAS token in the query string, for instance — and this is the last place to catch it.
/// </para>
/// </remarks>
public static class CrashLog
{
    private static readonly Lock Gate = new();

    /// <summary>Values known to be secret, masked wherever they appear in a trace.</summary>
    public static SecretRegistry? Secrets { get; set; }

    public static void Write(Exception exception, string? context = null)
    {
        try
        {
            Directory.CreateDirectory(StorageLocations.Logs);

            var path = Path.Combine(StorageLocations.Logs, $"courier-{DateTime.UtcNow:yyyyMMdd}.log");
            var text =
                $"{DateTimeOffset.UtcNow:O}  {context ?? "unhandled"}{Environment.NewLine}"
                + $"{exception}{Environment.NewLine}{Environment.NewLine}";

            var scrubbed = SecretPatterns.Scrub(Secrets?.Mask(text) ?? text);

            lock (Gate)
            {
                File.AppendAllText(path, scrubbed);
            }
        }
        catch (IOException)
        {
            // Failing to log must never be the thing that takes the process down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
