namespace Courier.Core.Tests;

/// <summary>
/// Finds the repository root from the test assembly, so source-scanning tests work from any
/// working directory and under any runner.
/// </summary>
internal static class RepoLayout
{
    public static string Root { get; } = Locate();

    public static string Src => Path.Combine(Root, "src");

    public static string Samples => Path.Combine(Root, "samples");

    private static string Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Courier.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find the repository root (no Courier.slnx above " + AppContext.BaseDirectory + ").");
    }
}
