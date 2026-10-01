namespace ReBackup.Core.Tests.TestSupport;

/// <summary>Paths in the repository (the folder that holds ReBackup.sln), found from the test's output folder.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string AppDirectory => Path.Combine(Root, "src", "ReBackup.App");

    public static string CoreDirectory => Path.Combine(Root, "src", "ReBackup.Core");

    public static string LocaleFile(string language) => Path.Combine(AppDirectory, "Locales", language + ".json");

    /// <summary>The project's files matching <paramref name="pattern"/>, without its bin and obj folders.</summary>
    public static IEnumerable<string> SourceFiles(string projectDirectory, string pattern) =>
        Directory.EnumerateFiles(projectDirectory, pattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(projectDirectory, path)));

    private static bool IsBuildOutput(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first.Equals("bin", StringComparison.OrdinalIgnoreCase) || first.Equals("obj", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReBackup.sln")))
                return directory.FullName;
        }
        throw new InvalidOperationException("ReBackup.sln was not found above " + AppContext.BaseDirectory);
    }
}
