using ReBackup.Control;
using ReBackup.Core.Indexing;
using ReBackup.Core.Versions;

namespace ReBackup.App.Control;

/// <summary>
/// The growth comparisons: where the growth between two versions, or between a version and what the next run would back
/// up, comes from, by folder (the direct children of <c>path</c>) and by extension.
/// </summary>
public sealed partial class ControlService
{
    private const int DefaultTop = 20;

    // ------------------------------------------------------------------ compare_versions

    private async Task<GrowthResult> CompareVersionsAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var nameA = args.RequiredString("a").Trim();
        var nameB = args.RequiredString("b").Trim();
        var path = NormalizePath(args.OptionalString("path") ?? "");
        var top = Top(args);
        var a = await ResolveVersionAsync(context, nameA, ct);
        var b = await ResolveVersionAsync(context, nameB, ct);
        return await Task.Run(() =>
        {
            var index = _main.VersionIndexes.For(context.Plan.Id);
            if (index.FindPath(path, isDirectory: true) is not { } folderId)
                throw new ControlFailure(ControlErrors.BadRequest, $"Neither version has a folder '{path}'.");
            var growth = index.Growth(a.Id, b.Id, folderId);
            return Growth(context.Plan.Name, a.Name, b.Name, path, growth.ByFolder, growth.ByExtension, top);
        }, ct);
    }

    // ------------------------------------------------------------------ compare_source_with_version

    /// <summary>
    /// The version against what the next run would back up: the source scan evaluated with the plan's rules, where only
    /// included files count. A file is unchanged when its size and time equal the version's (as an incremental run decides);
    /// a file of the version that is gone from the source or now ignored counts as deleted.
    /// </summary>
    private async Task<GrowthResult> CompareSourceWithVersionAsync(ControlArgs args, CancellationToken ct)
    {
        var context = await ResolvePlanAsync(args, ct);
        var path = NormalizePath(args.OptionalString("path") ?? "");
        var top = Top(args);
        var version = args.OptionalString("version") is { Length: > 0 } name && name.Trim().Length > 0
            ? await ResolveVersionAsync(context, name.Trim(), ct)
            : await NewestVersionAsync(context, ct);
        var scan = await ScanAsync(context, ct);
        return await Task.Run(() =>
        {
            var folder = SourceFolder(IndexEvaluator.Evaluate(scan, Matcher(context, scan), ct), path);
            var folderPath = folder.RelativePath;
            var index = _main.VersionIndexes.For(context.Plan.Id);
            var inVersion = (index.FindPath(folderPath, isDirectory: true) is { } folderId ? index.FilesBelow(version.Id, folderId) : [])
                .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);   // the index's paths ignore case too

            var byFolder = new GrowthTally();
            var byExtension = new GrowthTally();
            void Add(string filePath, long? older, long? newer)
            {
                byFolder.Add(GrowthTally.ChildKey(folderPath, filePath), older, newer);
                byExtension.Add(GrowthTally.ExtensionKey(filePath), older, newer);
            }

            var pending = new Stack<EvaluatedNode>([folder]);
            while (pending.TryPop(out var node))
            {
                ct.ThrowIfCancellationRequested();
                foreach (var child in node.Children)
                {
                    if (child.Status == IncludeStatus.Ignored)
                        continue;
                    if (child.IsDirectory)
                    {
                        pending.Push(child);
                        continue;
                    }
                    var filePath = child.RelativePath;
                    if (!inVersion.Remove(filePath, out var backedUp))
                        Add(filePath, null, child.Node.Size);
                    else if (backedUp.Size != child.Node.Size || backedUp.MtimeUtc.Ticks != child.Node.LastWriteUtc.Ticks)
                        Add(filePath, backedUp.Size, child.Node.Size);
                }
            }
            foreach (var gone in inVersion.Values)
                Add(gone.Path, gone.Size, null);

            return Growth(context.Plan.Name, version.Name, SourceListing, folderPath, byFolder.Rows(), byExtension.Rows(), top);
        }, ct);
    }

    /// <summary>
    /// The plan's newest version: the newest folder in the target (<c>version_not_indexed</c> while the index lacks it),
    /// else the newest the index holds when the target cannot be read; <c>version_not_found</c> before the first run.
    /// </summary>
    private async Task<IndexedVersion> NewestVersionAsync(PlanContext context, CancellationToken ct)
    {
        var folders = await ListTargetAsync(context.Plan, ct);   // oldest first (VersionCatalog sorts by date, then name)
        if (folders is { Count: > 0 })
            return await ResolveVersionAsync(context, folders[^1].Name, ct);
        var indexed = await Task.Run(() => _main.VersionIndexes.For(context.Plan.Id).Versions(), ct);
        return indexed.Count > 0
            ? indexed[^1]
            : throw new ControlFailure(ControlErrors.VersionNotFound,
                $"The plan '{context.Plan.Name}' has no versions yet; its next run backs up everything included.");
    }

    // ------------------------------------------------------------------ the result

    private static int Top(ControlArgs args) => Math.Clamp(args.OptionalInt("top") ?? DefaultTop, 1, MaxRows);

    /// <summary>
    /// The result: the totals of every row, then the first <paramref name="top"/> rows of each list, fewer when the
    /// character budget needs it.
    /// </summary>
    private static GrowthResult Growth(string plan, string from, string to, string path, IReadOnlyList<GrowthRow> byFolder,
        IReadOnlyList<GrowthRow> byExtension, int top)
    {
        var total = GrowthRowInfo.TotalOf(byFolder);
        var folders = byFolder.Take(top).Select(GrowthRowInfo.Of).ToList();
        var extensions = byExtension.Take(top).Select(GrowthRowInfo.Of).ToList();
        return FitToBudget(Math.Max(folders.Count, extensions.Count), count =>
        {
            var shownFolders = Math.Min(count, folders.Count);
            var shownExtensions = Math.Min(count, extensions.Count);
            var truncated = shownFolders < byFolder.Count || shownExtensions < byExtension.Count;
            return new GrowthResult(plan, from, to, path, total, folders.Take(shownFolders).ToList(), byFolder.Count,
                extensions.Take(shownExtensions).ToList(), byExtension.Count, truncated,
                truncated
                    ? $"Showing the top {shownFolders} of {byFolder.Count} folder rows and {shownExtensions} of {byExtension.Count} " +
                      $"extension rows (the totals cover all). Drill into a folder with 'path' (e.g. the first folder row), " +
                      $"or change 'top' (at most {MaxRows})."
                    : null);
        });
    }
}
