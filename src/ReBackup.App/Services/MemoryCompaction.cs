using System.Runtime;

namespace ReBackup.App.Services;

/// <summary>Hands memory that is no longer used back to Windows now.</summary>
public static class MemoryCompaction
{
    /// <summary>
    /// After a scan of millions of entries, the live tree it grew and any older evaluation are garbage of the same size as
    /// the result; the runtime would keep that memory until a full collection happens to run. A blocking, compacting
    /// collection (large object heap included) returns it now.
    /// </summary>
    public static void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
