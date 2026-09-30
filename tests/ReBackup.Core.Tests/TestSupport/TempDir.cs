namespace ReBackup.Core.Tests.TestSupport;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rebackup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) => System.IO.Path.Combine(Root, relative);

    public string CreateDir(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
