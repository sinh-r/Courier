using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Courier.MSBuild
{
    /// <summary>
    /// Runs the Courier scanner as a build step so the collection updates on every build without
    /// the app open. SCAN-11.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This targets netstandard2.0 because an MSBuild task loads into the build host, which may be
    /// .NET Framework under Visual Studio. That is also why it cannot reference Courier.Scanner
    /// directly — the scanner is net10.0 — so it shells out to the <c>courier</c> tool instead.
    /// </para>
    /// <para>
    /// The task never fails the build by default. A developer building a solution did not ask to
    /// scan it, and a scanner problem must not stand between them and their compiled output; set
    /// <see cref="TreatScanErrorsAsWarnings"/> to false in CI where you do want it to be a gate.
    /// </para>
    /// </remarks>
    public sealed class CourierScanTask : Microsoft.Build.Utilities.Task
    {
        /// <summary>Folder or .sln to scan. Usually the project directory.</summary>
        [Required]
        public string SourcePath { get; set; }

        /// <summary>Collection folder to write endpoints.generated.yaml into.</summary>
        [Required]
        public string CollectionPath { get; set; }

        /// <summary>The courier tool. Defaults to whatever is on PATH.</summary>
        public string ToolPath { get; set; } = "courier";

        /// <summary>Variable name to prefix generated URLs with.</summary>
        public string BaseUrlVariable { get; set; } = "baseUrl";

        /// <summary>
        /// True by default. A scan that cannot run is reported and the build continues, because
        /// the alternative is a developer unable to compile because of a tool they did not invoke.
        /// </summary>
        public bool TreatScanErrorsAsWarnings { get; set; } = true;

        /// <summary>Seconds before the scan is abandoned. PERF-06 targets three; ten is generous.</summary>
        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>Endpoints found, for a project that wants to log or gate on the count.</summary>
        [Output]
        public int EndpointCount { get; private set; }

        [Output]
        public int UnresolvedCount { get; private set; }

        public override bool Execute()
        {
            if (!Directory.Exists(SourcePath))
            {
                return Report($"Courier: '{SourcePath}' does not exist, so nothing was scanned.");
            }

            var reportPath = Path.Combine(Path.GetTempPath(), "courier-scan-" + Guid.NewGuid().ToString("n") + ".json");

            try
            {
                var arguments = new StringBuilder();
                arguments.Append("scan \"").Append(SourcePath.TrimEnd('\\')).Append('"');
                arguments.Append(" --output \"").Append(CollectionPath.TrimEnd('\\')).Append('"');
                arguments.Append(" --json \"").Append(reportPath).Append('"');
                arguments.Append(" --base-url-variable ").Append(BaseUrlVariable);

                var startInfo = new ProcessStartInfo(ToolPath, arguments.ToString())
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = SourcePath,
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return Report("Courier: the scanner could not be started.");
                    }

                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(TimeoutSeconds * 1000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                            // Already gone between the timeout and the kill.
                        }

                        return Report($"Courier: the scan did not finish within {TimeoutSeconds}s and was stopped.");
                    }

                    foreach (var line in output.Split('\n'))
                    {
                        if (line.Trim().Length > 0)
                        {
                            Log.LogMessage(MessageImportance.Normal, "Courier: " + line.TrimEnd());
                        }
                    }

                    if (process.ExitCode != 0)
                    {
                        return Report("Courier: the scan reported a problem. " + error.Trim());
                    }
                }

                ReadCounts(reportPath);

                Log.LogMessage(
                    MessageImportance.High,
                    string.Format(
                        "Courier: {0} endpoints written to {1}{2}",
                        EndpointCount,
                        CollectionPath,
                        UnresolvedCount > 0 ? ", " + UnresolvedCount + " unresolved" : string.Empty));

                // REQUIREMENTS 9: unresolved endpoints are surfaced, never silent. In a build that
                // means a warning the developer actually sees in the error list.
                if (UnresolvedCount > 0)
                {
                    Log.LogWarning(
                        "Courier: {0} endpoint(s) could not be fully derived. Run 'courier scan' to see why.",
                        UnresolvedCount);
                }

                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return Report(
                    "Courier: the 'courier' tool is not on PATH. Install it with "
                    + "'dotnet tool install -g Courier.Cli', or set ToolPath.");
            }
            catch (IOException ex)
            {
                return Report("Courier: " + ex.Message);
            }
            finally
            {
                TryDelete(reportPath);
            }
        }

        private void ReadCounts(string reportPath)
        {
            if (!File.Exists(reportPath))
            {
                return;
            }

            // Counted by scanning the report rather than parsing it: netstandard2.0 has no
            // System.Text.Json, and taking a JSON dependency into the build host to count two
            // numbers is not a trade worth making.
            var json = File.ReadAllText(reportPath);
            EndpointCount = CountOccurrences(json, "\"declaringType\"") - CountOccurrences(json, "\"reason\"");
            UnresolvedCount = CountOccurrences(json, "\"reason\"");

            if (EndpointCount < 0)
            {
                EndpointCount = 0;
            }
        }

        private static int CountOccurrences(string text, string token)
        {
            var count = 0;
            var index = 0;

            while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }

            return count;
        }

        private bool Report(string message)
        {
            if (TreatScanErrorsAsWarnings)
            {
                Log.LogWarning(message);
                return true;
            }

            Log.LogError(message);
            return false;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // A stray temp file is not worth failing a build over.
            }
        }
    }
}
