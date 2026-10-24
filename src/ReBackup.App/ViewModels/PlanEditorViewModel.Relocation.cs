using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Shared.Wpf.Localization;

namespace ReBackup.App.ViewModels;

/// <summary>
/// "Integrate into plan…" (a <c>.backupignore</c> becomes plan lines) and "Move selected rules to …/.backupignore…" (plan
/// lines become a nested file): a proposal, the confirmation dialog, then the move. Folder sources only, saved plans only.
/// </summary>
public sealed partial class PlanEditorViewModel
{
    private readonly Func<IReadOnlyList<string>> _globalIgnoreDefaults;
    private readonly IRecycleBin _recycleBin;
    private readonly Func<PlanEditorViewModel, bool> _save;

    /// <summary>Why neither move is possible now (not a folder source, unsaved edits); null when they are.</summary>
    public string? RelocationDisabledReason =>
        RelocationRoot() is null ? Loc.T("ignore.relocation.notFolder")
        : IsDirty ? Loc.T("ignore.relocation.saveFirst")
        : null;

    /// <summary>Why "Move selected rules" is not possible now; also when nested files are not honored (the file would do nothing).</summary>
    public string? ExtractDisabledReason =>
        RelocationDisabledReason ?? (HonorNestedIgnoreFiles ? null : Loc.T("ignore.relocation.honorOff"));

    /// <summary>"Move selected rules to sub/.backupignore…" for the folder selected in the tree.</summary>
    public string ExtractMenuText => Loc.F("ignore.tree.extract", ("folder", ExtractFolder() switch
    {
        { RelativePath.Length: > 0 } folder => folder.RelativePath,
        { } root => root.Name,
        null => "…",
    }));

    [RelayCommand(CanExecute = nameof(CanIntegrateIgnoreFile))]
    private void IntegrateIgnoreFile()
    {
        if (CanIntegrateIgnoreFile() && IntegrateDirectory() is { } directory)
            Relocate(context => IgnoreRelocation.ProposeIntegrate(context, directory));
    }

    private bool CanIntegrateIgnoreFile() => RelocationDisabledReason is null && IntegrateDirectory() is not null;

    /// <param name="lines">The plan lines selected in the pattern box; none takes every line that can move.</param>
    [RelayCommand(CanExecute = nameof(CanExtractToFolder))]
    private void ExtractToFolder(IReadOnlyList<int>? lines)
    {
        if (!CanExtractToFolder(lines) || ExtractFolder() is not { } folder)
            return;
        var directory = folder.RelativePath;
        var settings = CurrentIgnoreSettings();
        List<int> chosen = [.. (lines ?? []).Where(i => i >= 0 && i < settings.Patterns.Count).Distinct().Order()];
        if (chosen.Count == 0)
            chosen = [.. IgnoreRelocation.ExtractableLines(settings, directory)];
        Relocate(context => IgnoreRelocation.ProposeExtract(context, chosen, directory));
    }

    private bool CanExtractToFolder(IReadOnlyList<int>? lines) => ExtractDisabledReason is null && ExtractFolder() is not null;

    /// <summary>
    /// The indexes of the lines of <paramref name="text"/> that a selection from <paramref name="start"/> of
    /// <paramref name="length"/> characters touches; none for an empty selection. A selection that ends right after a line
    /// break does not touch the next line.
    /// </summary>
    public static IReadOnlyList<int> TouchedPatternLines(string text, int start, int length)
    {
        if (length <= 0 || start < 0 || start >= text.Length)
            return [];
        // The last selected character: a selection ending on a line break ("a\r\n") stays on that line.
        var end = Math.Min(text.Length, start + length) - 1;
        var first = LineAt(text, start);
        var last = LineAt(text, end);
        return Enumerable.Range(first, last - first + 1).ToList();
    }

    private static int LineAt(string text, int index)
    {
        var line = 0;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n')
                line++;
        return line;
    }

    /// <summary>The source folder, when the source is one; relocation works on folders only.</summary>
    private string? RelocationRoot() =>
        SourceLocation() is { IsFileSystem: true, Path: { } path } && !string.IsNullOrWhiteSpace(path) ? path : null;

    /// <summary>The folder of the <c>.backupignore</c> to integrate: the one the panel shows, or the file selected in the tree.</summary>
    private string? IntegrateDirectory()
    {
        if (Preview.SelectedIgnoreFile is { } shown)
            return shown.Directory;
        if (SelectedEntry() is { IsDirectory: false } node && IgnoreOrigins.IsNestedFileName(node.Name))
        {
            var slash = node.RelativePath.LastIndexOf('/');
            return slash < 0 ? "" : node.RelativePath[..slash];
        }
        return null;
    }

    /// <summary>The folder selected in the tree (the source root too).</summary>
    private IPreviewEntry? ExtractFolder() =>
        Preview.SelectedNode is { IsDirectory: true } node && node is not LoadingPlaceholder ? node : null;

    private void RefreshRelocation()
    {
        OnPropertyChanged(nameof(RelocationDisabledReason));
        OnPropertyChanged(nameof(ExtractDisabledReason));
        OnPropertyChanged(nameof(ExtractMenuText));
        IntegrateIgnoreFileCommand.NotifyCanExecuteChanged();
        ExtractToFolderCommand.NotifyCanExecuteChanged();
    }

    private void Relocate(Func<RelocationContext, RelocationProposal> propose)
    {
        if (RelocationRoot() is not { } root)
            return;
        var dialogs = _context.Dialogs;
        var title = Loc.T("ignore.relocation.title");
        RelocationProposal proposal;
        try
        {
            proposal = propose(new RelocationContext(CurrentIgnoreSettings(), _globalIgnoreDefaults(), Preview.CompletedIndex, root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.ShowError(title, Loc.F("ignore.relocation.failed", ("error", ex.Message)));
            return;
        }
        // The running scan is not complete: the scope cannot be compared until it is.
        if (Preview.IsIndexing)
            proposal = proposal with { Equivalence = new EquivalenceResult.NotChecked(EquivalenceResult.NotChecked.ScanRunning) };

        var model = new RelocationDialogViewModel(proposal);
        if (dialogs.ConfirmRelocation(model) && model.CanApply)
            Apply(proposal, root);
    }

    private void Apply(RelocationProposal proposal, string root)
    {
        var dialogs = _context.Dialogs;
        var title = Loc.T("ignore.relocation.title");
        // Only shown when the drive has no recycle bin and the file is renamed instead (the first free name).
        var renamedTo = Path.GetFileName(IgnoreRelocation.IntegratedFileName(
            Path.Combine(root, proposal.Directory.Replace('/', Path.DirectorySeparatorChar))));
        RecycleOutcome? outcome;
        try
        {
            outcome = IgnoreRelocation.Apply(proposal, root, CurrentIgnoreSettings().Patterns, SavePatterns, _recycleBin);
        }
        catch (RelocationStaleException ex)
        {
            dialogs.ShowError(title, Loc.T(ex.Message));
            return;
        }
        catch (PlanNotSavedException)
        {
            Revert();   // what is saved now: the plan as before (or as the undo left it)
            dialogs.ShowError(title, Loc.T("ignore.relocation.notSaved"));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Revert();
            dialogs.ShowError(title, Loc.F("ignore.relocation.failed", ("error", ex.Message)));
            return;
        }

        Preview.ReplaceNestedIgnoreFile(proposal.Directory,
            proposal.Kind == RelocationKind.Integrate ? null : RelocationDialogViewModel.LinesOf(proposal.FileContentAfter));
        if (outcome == RecycleOutcome.Renamed)
            dialogs.ShowInfo(title, Loc.F("ignore.relocation.renamed", ("name", renamedTo)));
    }

    /// <summary>Puts <paramref name="patterns"/> into the pattern box and saves the plan as the Save button does.</summary>
    private void SavePatterns(IReadOnlyList<string> patterns)
    {
        IgnorePatternsText = string.Join(Environment.NewLine, patterns);
        if (!_save(this))
            throw new PlanNotSavedException();
    }

    /// <summary>The save failed (and was reported); the move is undone.</summary>
    private sealed class PlanNotSavedException : Exception;
}
