namespace ReBackup.Core.IO;

public static class PathUtil
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsSameOrInside(string path, string root)
    {
        var p = WithTrailingSeparator(Normalize(path));
        var r = WithTrailingSeparator(Normalize(root));
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
