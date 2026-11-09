using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A version in the grid of the Cleanup tab: its row of the Versions tab, whether it is checked, what the last scan found
/// in it and what the last cleanup did with it. Kept by name across reloads.
/// </summary>
public sealed class CleanupRowViewModel : ObservableObject
{
    private VersionRowViewModel _version;
    private bool _isChecked;
    private (int Files, long Bytes, bool Pool)? _scan;
    private LocText? _status;

    public CleanupRowViewModel(VersionRowViewModel version) => _version = version;

    /// <summary>
    /// The row of the Versions tab this row shows; replaced on every reload (its index entry changes with a sync). A row
    /// that is no longer selectable is unchecked.
    /// </summary>
    public VersionRowViewModel Version
    {
        get => _version;
        set
        {
            _version = value;
            OnPropertyChanged(string.Empty);
            if (!IsSelectable)
                IsChecked = false;
        }
    }

    /// <summary>
    /// The version is cleaned up with the others checked. Only a selectable row can be checked: setting it on another
    /// row leaves it unchecked.
    /// </summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value && IsSelectable);
    }

    public string Name => Version.Name;

    public string DateText => Version.DateText;

    public string SizeText => Version.SizeText;

    public string FilesText => Version.FilesText;

    /// <summary>
    /// The version can be cleaned up: the plan manages it and it is indexed and intact (the scan reads its index entry,
    /// the run rewrites its manifest).
    /// </summary>
    public bool IsSelectable => Version is { IsManaged: true, Indexed: not null, IsDamaged: false };

    /// <summary>Why the row cannot be checked (not managed, damaged, not indexed yet); null for a selectable row.</summary>
    public string? NotSelectableReason =>
        !Version.IsManaged ? Version.ReasonText
        : Version.IsDamaged ? Loc.T("versions.damaged.toolTip")
        : Version.Indexed is null ? Loc.T("cleanup.notIndexed")
        : null;

    /// <summary>The files a scan found to remove, e.g. "2"; empty while the version was not scanned.</summary>
    public string RemovesText => _scan is { } scan ? Formats.Count(scan.Files) : "";

    /// <summary>
    /// The bytes a scan found to free in the version folder; "—" for pool plans (their content is shared, so only the
    /// total is known); empty while the version was not scanned.
    /// </summary>
    public string FreesText => _scan switch
    {
        null => "",
        { Pool: true } => "—",
        { } scan => Formats.Bytes(scan.Bytes),
    };

    /// <summary>
    /// The version's own size once the files the scan found are removed, e.g. "5.7 GB" (also for pool plans: what the
    /// version lists afterwards); empty while the version was not scanned or its size is unknown.
    /// </summary>
    public string SizeAfterText =>
        _scan is { } scan && (Version.Info.TotalBytes ?? Version.Indexed?.TotalBytes) is { } bytes
            ? Formats.Bytes(Math.Max(0, bytes - scan.Bytes))
            : "";

    /// <summary>What the last cleanup did with the version (or is doing now); empty before a cleanup.</summary>
    public string StatusText => _status?.ToString() ?? "";

    /// <summary>Shows what a scan found in the version: <paramref name="files"/> of <paramref name="bytes"/>.</summary>
    internal void ShowScan(int files, long bytes, bool pool)
    {
        _scan = (files, bytes, pool);
        OnPropertyChanged(nameof(RemovesText));
        OnPropertyChanged(nameof(FreesText));
        OnPropertyChanged(nameof(SizeAfterText));
    }

    /// <summary>Forgets the scan's numbers (not scanned, or the scan was dropped).</summary>
    internal void ClearScan()
    {
        _scan = null;
        OnPropertyChanged(nameof(RemovesText));
        OnPropertyChanged(nameof(FreesText));
        OnPropertyChanged(nameof(SizeAfterText));
    }

    /// <summary>The colour of the status lamp: what the last cleanup did with the version (or is doing now).</summary>
    public CleanupLamp Lamp { get; private set; }

    /// <summary>Sets <see cref="StatusText"/> and <see cref="Lamp"/>; null clears both.</summary>
    internal void SetStatus(LocText? status, CleanupLamp lamp = CleanupLamp.None)
    {
        _status = status;
        Lamp = status is null ? CleanupLamp.None : lamp;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Lamp));
    }

    /// <summary>The language changed: every text of the row is read again.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>
/// The status lamp of a version in the Cleanup tab: none before a cleanup; running while it is cleaned up; done when it
/// was cleaned up; idle when there was nothing to remove; warning when it was skipped (changed, gone) or the run stopped
/// before it; failed on an error.
/// </summary>
public enum CleanupLamp { None, Running, Done, Idle, Warning, Failed }
