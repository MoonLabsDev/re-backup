using System.Text;

namespace ReBackup.Core.IO;

public static class AtomicFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, contents, Utf8NoBom);
        File.Move(tempPath, fullPath, overwrite: true);
    }
}
