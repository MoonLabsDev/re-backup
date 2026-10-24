namespace ReBackup.Core.Ignore;

/// <summary>How a file was taken out of the way without being deleted.</summary>
public enum RecycleOutcome { Recycled, Renamed }

/// <summary>Takes a file out of the way without deleting it: into the recycle bin, or renamed where there is none.</summary>
public interface IRecycleBin
{
    RecycleOutcome Recycle(string path);
}

/// <summary>The plan or the <c>.backupignore</c> changed after the proposal was made; the message is the locale key.</summary>
public sealed class RelocationStaleException() : Exception("ignore.relocation.stale");
