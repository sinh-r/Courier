using System.Text;
using Courier.Core.Storage;

namespace Courier.Core.Privacy;

/// <summary>
/// The auditable, exportable statement of network behaviour a security reviewer signs off on.
/// SEC-06.
/// </summary>
/// <remarks>
/// Rendered from <see cref="EgressGate"/>'s own records rather than written by hand, so it cannot
/// claim something the code does not do. If a category appears here that the reviewer did not
/// expect, the code really did open that connection.
/// </remarks>
public static class NetworkStatement
{
    public static string Render(
        IReadOnlyList<EgressRecord> records,
        UserIntentEgressPolicy policy,
        string applicationVersion,
        DateTimeOffset generatedUtc)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Courier — network behaviour statement");
        sb.AppendLine("=====================================");
        sb.AppendLine();
        sb.AppendLine($"Courier version   {applicationVersion}");
        sb.AppendLine($"Generated         {generatedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Session records   {records.Count}");
        sb.AppendLine();

        sb.AppendLine("Architectural guarantees");
        sb.AppendLine("------------------------");
        sb.AppendLine("  1. Courier has no account system, no sync and no server operated by the project.");
        sb.AppendLine("     There is nowhere for your collections or responses to be uploaded to.");
        sb.AppendLine("  2. Requests go directly from this process to the host named in the request.");
        sb.AppendLine("     No relay, proxy or inspection service belonging to the project sits in between.");
        sb.AppendLine("  3. Every socket in this process is opened by one class, Courier.Core.Privacy.EgressGate.");
        sb.AppendLine("     A build-time test fails the build if any other code constructs an HTTP handler.");
        sb.AppendLine("  4. Secrets are held by the operating system credential store. No file Courier");
        sb.AppendLine("     writes contains a secret value, including log files at verbose level.");
        sb.AppendLine($"  5. Update checking is {(policy.UpdateCheckOptedIn ? "ON — you switched it on" : "OFF — the default, and it stays off unless you switch it on")}.");
        sb.AppendLine("     There is no analytics and no crash reporting, opt-in or otherwise.");
        sb.AppendLine();

        sb.AppendLine("Destinations contacted this session");
        sb.AppendLine("-----------------------------------");

        if (records.Count == 0)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var group in records.GroupBy(r => r.Purpose).OrderBy(g => g.Key))
            {
                sb.AppendLine();
                sb.AppendLine($"  {DescribePurpose(group.Key)}");

                foreach (var byHost in group.GroupBy(r => r.Destination).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var allowed = byHost.Count(r => r.WasAllowed);
                    var denied = byHost.Count() - allowed;
                    var initiators = string.Join(", ", byHost.Select(r => r.Initiator).Distinct().Order());
                    var first = byHost.Min(r => r.AtUtc);
                    var last = byHost.Max(r => r.AtUtc);

                    sb.AppendLine($"    {byHost.Key}");
                    sb.AppendLine($"      connections   {allowed} allowed{(denied > 0 ? $", {denied} refused" : string.Empty)}");
                    sb.AppendLine($"      requested by  {initiators}");
                    sb.AppendLine($"      first / last  {first:HH:mm:ss} / {last:HH:mm:ss} UTC");

                    var denial = byHost.FirstOrDefault(r => !r.WasAllowed)?.DenialReason;
                    if (denial is not null)
                    {
                        sb.AppendLine($"      refused because  {denial}");
                    }
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("Destinations this profile authorises");
        sb.AppendLine("------------------------------------");

        if (policy.Registered.Count == 0)
        {
            sb.AppendLine("  (none configured)");
        }
        else
        {
            foreach (var (host, purpose) in policy.Registered.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {host,-48} {DescribePurpose(purpose)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Where data is stored on this machine");
        sb.AppendLine("------------------------------------");
        foreach (var location in StorageLocations.Describe())
        {
            sb.AppendLine($"  {location.Category,-22} {location.Path}");
            sb.AppendLine($"  {string.Empty,-22} {location.Contents}");
        }

        return sb.ToString();
    }

    private static string DescribePurpose(EgressPurpose purpose) => purpose switch
    {
        EgressPurpose.TargetRequest => "Requests you composed and sent",
        EgressPurpose.AuthAuthority => "Token authorities named by your auth profiles",
        EgressPurpose.TelemetryBackend => "Monitoring backends you connected",
        EgressPurpose.WorkItemTracker => "Work item tracker, only when you attach or open a capsule",
        EgressPurpose.ProxyAutoConfig => "Proxy auto-configuration script named by this machine's proxy settings",
        EgressPurpose.UpdateCheck => "Update check (off unless you switched it on)",
        _ => purpose.ToString(),
    };
}
