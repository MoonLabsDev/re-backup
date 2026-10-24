namespace ReBackup.Core.Ignore;

/// <summary>Sends a file to the operating system's recycle bin instead of deleting it.</summary>
public interface IRecycleBin
{
    void Recycle(string path);
}

/// <summary>The plan or the <c>.backupignore</c> changed after the proposal was made; the message is the locale key.</summary>
public sealed class RelocationStaleException() : Exception("ignore.relocation.stale");
