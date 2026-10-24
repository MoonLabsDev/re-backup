using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>One <c>.backupignore</c> of the source in the chip's flat list; <see cref="Path"/> is relative to the source root.</summary>
public sealed record IgnoreFileListItem(string Directory, string Path);

/// <summary>
/// One line of an ignore file. <see cref="Entries"/> is null for comments and blank lines; <see cref="IsUnused"/> means
/// a pattern that currently decides nothing.
/// </summary>
public sealed record IgnoreFileLine(int Number, string Text, int? Entries, string BytesText, bool IsUnused)
{
    /// <summary>"3 entries · 1.2 MB", "no hits" for an unused pattern, "" for a comment or blank line.</summary>
    public string HitsText => Entries is not { } entries
        ? ""
        : IsUnused
            ? Loc.T("ignore.files.unused")
            : Loc.F("ignore.files.hits", ("entries", entries), ("bytes", BytesText));
}

/// <summary>The panel's content: the file's lines with what each pattern line decides in the current evaluation.</summary>
public sealed record IgnoreFileDetail(string Directory, string Path, IReadOnlyList<IgnoreFileLine> Lines);

/// <summary>The Ignore tab's <c>.backupignore</c> chip (a flat list of the files) and the detail panel.</summary>
public sealed partial class IgnorePreviewViewModel
{
    private readonly Func<string, bool> _openFile;
    private readonly Func<string, bool> _openInNotepad;
    private readonly IDialogService? _dialogs;

    /// <summary>The flat list of the source's readable <c>.backupignore</c> files, sorted by path.</summary>
    public RangeObservableCollection<IgnoreFileListItem> IgnoreFileItems { get; } = new();

    /// <summary>The chip: the tree is replaced by <see cref="IgnoreFileItems"/>. Per session, not saved.</summary>
    [ObservableProperty] private bool _showIgnoreFilesOnly;

    /// <summary>The file chosen in the flat list.</summary>
    [ObservableProperty] private IgnoreFileListItem? _selectedIgnoreFileItem;

    /// <summary>The file the panel shows; null closes the panel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IgnoreFileEditorToolTip))]
    [NotifyCanExecuteChangedFor(nameof(OpenIgnoreFileInEditorCommand))]
    private IgnoreFileDetail? _selectedIgnoreFile;

    /// <summary>The finished scan; null while one runs and after it was freed.</summary>
    public SourceIndex? CompletedIndex => IsIndexing ? null : _index;

    public int IgnoreFileCount => IgnoreFileItems.Count;

    public string IgnoreFilesChipText => Loc.F("ignore.files.chip", ("count", IgnoreFileCount));

    /// <summary>Why "Open in editor" is disabled; null when it is not.</summary>
    public string? IgnoreFileEditorToolTip => IsFolderSource ? null : Loc.T("ignore.files.folderOnly");

    private bool IsFolderSource => _source()?.IsFileSystem == true;

    partial void OnIsIndexingChanged(bool value) => OnPropertyChanged(nameof(CompletedIndex));

    partial void OnShowIgnoreFilesOnlyChanged(bool value)
    {
        SelectedIgnoreFileItem = null;
        if (value)
            CloseIgnoreFilePanel();
        else
            OpenForNode(SelectedNode);
    }

    partial void OnSelectedIgnoreFileItemChanged(IgnoreFileListItem? value)
    {
        if (ShowIgnoreFilesOnly)
            ShowIgnoreFile(value?.Directory, value?.Path);
    }

    /// <summary>The tree's selection moved: a <c>.backupignore</c> opens the panel, anything else closes it.</summary>
    private void OpenForNode(IPreviewEntry? node)
    {
        if (ShowIgnoreFilesOnly)
            return;
        if (node is { IsDirectory: false } && IgnoreOrigins.IsNestedFileName(node.Name))
        {
            var path = node.RelativePath;
            var slash = path.LastIndexOf('/');
            ShowIgnoreFile(slash < 0 ? "" : path[..slash], path);
        }
        else
        {
            CloseIgnoreFilePanel();
        }
    }

    private void CloseIgnoreFilePanel() => SelectedIgnoreFile = null;

    /// <summary>Fills the panel from the scanned lines (what the matcher used) and the current evaluation.</summary>
    private void ShowIgnoreFile(string? directory, string? path)
    {
        if (directory is null || path is null || Root is not { } root || _index is not { } index ||
            index.IgnoreFiles.FirstOrDefault(f => string.Equals(f.DirectoryRelativePath, directory, StringComparison.OrdinalIgnoreCase))
                is not { } file)
        {
            CloseIgnoreFilePanel();
            return;
        }

        var hits = IgnoreLineStats.ForOrigin(root, IgnoreOrigins.ForNestedFile(file.DirectoryRelativePath), file.Lines)
            .ToDictionary(h => h.LineNumber);
        var lines = new List<IgnoreFileLine>(file.Lines.Count);
        for (var i = 0; i < file.Lines.Count; i++)
        {
            lines.Add(hits.TryGetValue(i + 1, out var h)
                ? new IgnoreFileLine(i + 1, file.Lines[i], h.Entries, Formats.Bytes(h.Bytes), h.Entries == 0)
                : new IgnoreFileLine(i + 1, file.Lines[i], null, "", false));
        }
        SelectedIgnoreFile = new IgnoreFileDetail(file.DirectoryRelativePath, path, lines);
    }

    /// <summary>A new evaluation is published: the list and the open panel are made from it.</summary>
    private void RefreshIgnoreFiles(SourceIndex index)
    {
        var open = SelectedIgnoreFile;
        var chosen = SelectedIgnoreFileItem?.Directory;
        IgnoreFileItems.ReplaceAll(index.IgnoreFiles
            .Select(f => new IgnoreFileListItem(f.DirectoryRelativePath, PathOf(index, f.DirectoryRelativePath)))
            .OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase));
        // The list view drops its selection when the collection is reset (and pushes null): choose the same file again.
        if (chosen is not null)
            SelectedIgnoreFileItem = IgnoreFileItems.FirstOrDefault(
                i => string.Equals(i.Directory, chosen, StringComparison.OrdinalIgnoreCase));
        RaiseIgnoreFileCount();
        if (open is not null)
            ShowIgnoreFile(open.Directory, open.Path);
    }

    /// <summary>The scan is dropped, freed or restarted: nothing of it stays in the list or the panel.</summary>
    private void ClearIgnoreFiles()
    {
        SelectedIgnoreFileItem = null;
        CloseIgnoreFilePanel();
        IgnoreFileItems.Clear();
        RaiseIgnoreFileCount();
    }

    private void RaiseIgnoreFileCount()
    {
        OnPropertyChanged(nameof(IgnoreFileCount));
        OnPropertyChanged(nameof(IgnoreFilesChipText));
        OnPropertyChanged(nameof(CompletedIndex));
    }

    /// <summary>The file's path with its real spelling (<c>.BackupIgnore</c>); the canonical name when the folder cannot be found.</summary>
    private static string PathOf(SourceIndex index, string directory)
    {
        var folder = index.RootNode;
        foreach (var part in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            folder = folder.Children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (folder is null)
                return IgnoreOrigins.ForNestedFile(directory);
        }
        var name = folder.Children.FirstOrDefault(c => !c.IsDirectory && IgnoreOrigins.IsNestedFileName(c.Name))?.Name
                   ?? IgnoreOrigins.NestedFileName;
        return directory.Length == 0 ? name : directory + "/" + name;
    }

    [RelayCommand(CanExecute = nameof(CanOpenIgnoreFileInEditor))]
    private void OpenIgnoreFileInEditor()
    {
        if (SelectedIgnoreFile is not { } file || _source() is not { IsFileSystem: true } source)
            return;
        var path = Path.GetFullPath(Path.Combine(source.Path, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        // ".backupignore" has no file association on most machines: the shell fails, Notepad always exists.
        if (_openFile(path) || _openInNotepad(path))
            return;
        _dialogs?.ShowError(Loc.T("ignore.files.panelTitle"), Loc.F("ignore.files.openFailed", ("path", path)));
    }

    private bool CanOpenIgnoreFileInEditor() => SelectedIgnoreFile is not null && IsFolderSource;
}
