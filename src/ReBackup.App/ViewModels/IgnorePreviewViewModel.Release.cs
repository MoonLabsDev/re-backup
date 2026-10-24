using System.Runtime;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A scan of a large source holds a lot of memory (about 200 bytes per file and folder). While the tab is not shown —
/// another tab or plan, the window minimized or in the tray — it is freed after <see cref="ReleaseDelay"/>; the next
/// "Index now" scans again.
/// </summary>
public sealed partial class IgnorePreviewViewModel
{
    public static readonly TimeSpan DefaultReleaseDelay = TimeSpan.FromMinutes(30);
    private CancellationTokenSource? _releaseCts;
    private bool _shown;

    /// <summary>How long the scan is kept while the tab is not shown.</summary>
    public TimeSpan ReleaseDelay { get; set; } = DefaultReleaseDelay;

    /// <summary>The tab is shown (true) or not; while it is not, the scan is freed once <see cref="ReleaseDelay"/> has passed.</summary>
    public void SetShown(bool shown)
    {
        if (shown == _shown)
            return;
        _shown = shown;
        _releaseCts?.Cancel();
        _releaseCts = null;
        if (!shown)
            ReleaseWhenUnused(_releaseCts = new CancellationTokenSource());
    }

    private async void ReleaseWhenUnused(CancellationTokenSource cts)
    {
        try
        {
            // A running scan is left alone; it is freed once it has been finished and unused for the whole delay.
            do
            {
                await Task.Delay(ReleaseDelay, cts.Token);
            }
            while (IsIndexing);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ReferenceEquals(_releaseCts, cts))
            Release();
    }

    /// <summary>
    /// Frees the scan: the tree stays empty until the next scan. The summary and the size of the last evaluation stay
    /// (the Retention tab's forecast falls back on it).
    /// </summary>
    public void Release()
    {
        if ((_index is null && Root is null) || IsIndexing)
            return;
        _evaluateCts?.Cancel();
        _evaluationVersion++;   // an evaluation still running must not publish the freed index again
        _index = null;
        _indexCts = null;
        _scan = null;
        Root = null;
        Tree.IgnoreFileDirectories = NoDirectories;
        Tree.SetRoot(null);
        ClearIgnoreFiles();
        Progress = LocText.Of("ignore.progress.released");
        CompactMemory();
    }

    /// <summary>
    /// Hands memory that is no longer used back to Windows now. After a scan of millions of entries, the live tree it
    /// grew and any older evaluation are garbage of the same size as the result; the runtime would keep that memory
    /// until a full collection happens to run.
    /// </summary>
    private static void CompactMemory()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
