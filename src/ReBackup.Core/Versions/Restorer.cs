using System.Globalization;
using System.Text.Json;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;
using ReBackup.Shared.IO;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.Core.Versions;

/// <summary>Original: back to where the version came from. ToFolder: into a chosen folder, under the item's name.</summary>
public enum RestoreMode { Original, ToFolder }

/// <summary>What happens to a file that already exists at the destination; asked once per restore.</summary>
public enum ConflictPolicy { Overwrite, Skip, KeepBoth }

/// <summary>
/// One file to copy: its path in the versions' storage, where it goes in the destination storage, and the modified time
/// it gets there (the manifest's, else the copy's; null when unknown).
/// </summary>
public sealed record RestoreFile(string Source, string Destination, long Size, DateTime? ModifiedUtc = null);

/// <summary>Everything a restore will do; made by <see cref="Restorer.PlanAsync"/>, nothing is written yet.</summary>
public sealed class RestorePlan
{
    /// <summary>The storage the version is read from (the plan's target).</summary>
    public required IStorage Versions { get; init; }

    /// <summary>The version's folder in <see cref="Versions"/>.</summary>
    public required string VersionPath { get; init; }

    /// <summary>The storage written to; <see cref="RestoreFile.Destination"/> and <see cref="Directories"/> are paths in it.</summary>
    public required IStorage Destination { get; init; }

    /// <summary>The version's time: the stamp of "keep both" names.</summary>
    public required DateTime VersionTime { get; init; }

    /// <summary>Folders to create (the selected folders and everything below them, also empty ones).</summary>
    public required IReadOnlyList<string> Directories { get; init; }

    public required IReadOnlyList<RestoreFile> Files { get; init; }

    /// <summary>Files whose destination exists already.</summary>
    public required IReadOnlyList<RestoreFile> Conflicts { get; init; }

    /// <summary>Parts of the version that could not be listed (unreadable folders); <see cref="Restorer.RunAsync"/> reports them as failures.</summary>
    public IReadOnlyList<RestoreFailure> PlanFailures { get; init; } = [];

    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <summary>
/// <paramref name="Path"/> is a path in the destination storage, or in the versions' storage when <paramref name="InVersion"/>
/// (a part of the version that could not be read).
/// </summary>
public sealed record RestoreFailure(string Path, string Reason, bool InVersion = false);

/// <summary>
/// <paramref name="Copied"/> counts files written to their own name (also overwritten ones), <paramref name="KeptBoth"/>
/// files written under a "keep both" name.
/// </summary>
public sealed record RestoreResult(int Copied, int Skipped, int KeptBoth, IReadOnlyList<RestoreFailure> Failures, bool Canceled);

public readonly record struct RestoreProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string CurrentFile)
{
    public double Fraction => BytesTotal > 0 ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : FilesTotal > 0 ? Math.Clamp((double)FilesDone / FilesTotal, 0, 1) : 0;
}

/// <summary>
/// Copies files and folders from a version back. Never deletes or moves anything at the destination; the only file
/// it replaces is a conflicting one under <see cref="ConflictPolicy.Overwrite"/>. Nothing is written through a link at
/// the destination, and links in the version are not followed.
/// </summary>
public static class Restorer
{
    private const int BufferSize = 1024 * 1024;

    /// <summary>How often a "keep both" file is tried under a further name when its name is taken while it is written.</summary>
    private const int KeepBothAttempts = 100;

    /// <summary>
    /// Lists what restoring <paramref name="relativePaths"/> (files or folders of the version, forward or back slashes,
    /// "" = the whole version) to <paramref name="destination"/> copies, and which files exist there already.
    /// Original: destination path = relative path. ToFolder: destination path = the item's name. The manifest supplies
    /// the files' modified times and the empty folders a storage without real folders cannot list.
    /// </summary>
    /// <exception cref="ArgumentException">A path leaves the version, does not exist, or is a link; the destination lies in the version.</exception>
    /// <exception cref="StorageNotFoundException">The version folder does not exist.</exception>
    /// <exception cref="StorageException">The version cannot be read.</exception>
    public static async Task<RestorePlan> PlanAsync(IStorage versions, string versionPath, IReadOnlyList<string> relativePaths,
        IStorage destination, RestoreMode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(destination);
        StoragePath.Validate(versionPath);
        if (await versions.StatAsync(versionPath, ct).ConfigureAwait(false) is not { IsDirectory: true } versionEntry)
            throw new StorageNotFoundException(versionPath,
                CoreTexts.English("core.restore.versionMissing", ("folder", DisplayPath(versions, versionPath))));
        if (versions is FileSystemStorage fromFolder && destination is FileSystemStorage toFolder &&
            PathUtil.IsSameOrInside(toFolder.RootPath, fromFolder.FullPathOf(versionPath)))
            throw new ArgumentException(CoreTexts.English("core.restore.destinationInVersion"), nameof(destination));

        var comparer = versions.Capabilities.HasFlag(StorageCapabilities.CaseSensitive)
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        string InVersion(string path) => versionPath.Length == 0 ? path : path[(versionPath.Length + 1)..];

        var selected = new List<(string Relative, string Destination, StorageEntry Entry)>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in relativePaths)
        {
            var relative = CheckRelative(relativePath);
            var entry = relative.Length == 0
                ? versionEntry
                : await FindInVersionAsync(versions, versionPath, relative, relativePath, ct).ConfigureAwait(false);
            var name = relative.Length == 0 ? StoragePath.Name(versionPath) : StoragePath.Name(relative);
            if (mode == RestoreMode.ToFolder)
            {
                if (names.TryGetValue(name, out var earlier) && !string.Equals(earlier, relative, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(CoreTexts.English("core.restore.sameName", ("name", name)), nameof(relativePaths));
                names[name] = relative;
            }
            if (!entry.IsDirectory && IsManifest(relative))
                throw new ArgumentException(CoreTexts.English("core.restore.manifest"), nameof(relativePaths));
            selected.Add((relative, mode == RestoreMode.Original ? relative : name, entry));
        }

        var manifest = await ReadManifestAsync(versions, versionPath, selected.Select(s => s.Relative).ToList(), comparer, ct)
            .ConfigureAwait(false);
        var directories = new List<string>();
        var knownDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<RestoreFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planFailures = new List<RestoreFailure>();
        var links = new List<string>();

        void AddDirectory(string path)
        {
            if (knownDirectories.Add(path))
                directories.Add(path);
        }

        void AddFile(StorageEntry source, string target)
        {
            if (!seen.Add(target))
                return;
            var modified = manifest is not null && manifest.Mtimes.TryGetValue(InVersion(source.Path), out var mtime)
                ? mtime
                : source.ModifiedUtc;
            files.Add(new RestoreFile(source.Path, target, source.Size, modified));
        }

        foreach (var (relative, target, entry) in selected)
        {
            if (!entry.IsDirectory)
            {
                AddFile(entry, target);
                continue;
            }

            var pending = new Stack<(string Source, string Destination)>();
            pending.Push((entry.Path, target));
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (source, destinationFolder) = pending.Pop();
                AddDirectory(destinationFolder);
                List<StorageEntry> children;
                try
                {
                    children = await ListAsync(versions, source, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is StorageException or ArgumentException)
                {
                    planFailures.Add(new RestoreFailure(source, Reason(ex), InVersion: true));
                    continue;
                }

                foreach (var child in children)
                {
                    if (child.IsLink)
                    {
                        links.Add(child.Path);   // links are not followed
                        continue;
                    }
                    var childTarget = StoragePath.Combine(destinationFolder, StoragePath.Name(child.Path));
                    if (child.IsDirectory)
                        pending.Push((child.Path, childTarget));
                    else if (!IsManifest(InVersion(child.Path)))
                        AddFile(child, childTarget);
                }
            }

            // Empty folders that a storage without real folders knows only from the manifest (format 2).
            foreach (var folder in manifest?.Directories ?? [])
            {
                if (!IsSameOrBelow(folder, relative, comparer))
                    continue;
                var inStorage = StoragePath.Combine(versionPath, folder);
                if (links.Any(link => IsSameOrBelow(inStorage, link, comparer)))
                    continue;
                var folderTarget = StoragePath.Combine(target, folder[relative.Length..].TrimStart('/'));
                if (!seen.Contains(folderTarget))
                    AddDirectory(folderTarget);
            }
        }

        var conflicts = new List<RestoreFile>();
        var linkFree = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            if (await ExistsOutsideLinksAsync(destination, file.Destination, linkFree, ct).ConfigureAwait(false))
                conflicts.Add(file);
        }

        return new RestorePlan
        {
            Versions = versions,
            VersionPath = versionPath,
            Destination = destination,
            VersionTime = VersionTimeOf(versionPath, manifest?.CreatedUtc, versionEntry),
            Directories = directories,
            Files = files,
            Conflicts = conflicts,
            PlanFailures = planFailures,
        };
    }

    /// <summary>
    /// Copies the plan's files through the destination's <see cref="StorageWriter"/>: a file appears complete or not at
    /// all, and gets the version's modified time. Folders are created only on storages with real folders. A file that
    /// cannot be written is recorded as a failure and the rest continues. Skip and KeepBoth never replace a file, also
    /// not one that appeared after planning. Cancellation stops before the next file (a half-written file is discarded).
    /// </summary>
    public static async Task<RestoreResult> RunAsync(RestorePlan plan, ConflictPolicy policy,
        IProgress<RestoreProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var destination = plan.Destination;
        var failures = new List<RestoreFailure>(plan.PlanFailures);
        int copied = 0, skipped = 0, keptBoth = 0, filesDone = 0;
        long bytesDone = 0;
        var bytesTotal = plan.TotalBytes;
        var buffer = new byte[BufferSize];
        RestoreResult Result(bool canceled) => new(copied, skipped, keptBoth, failures, canceled);

        if (destination.Capabilities.HasFlag(StorageCapabilities.EmptyDirectories))
        {
            foreach (var directory in plan.Directories)
            {
                if (ct.IsCancellationRequested)
                    return Result(canceled: true);
                try
                {
                    if (await FindLinkAsync(destination, directory, ct).ConfigureAwait(false))
                        failures.Add(new RestoreFailure(directory, CoreTexts.English("core.restore.destinationLink")));
                    else
                        await destination.EnsureDirectoryAsync(directory, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return Result(canceled: true);
                }
                catch (Exception ex) when (ex is StorageException or ArgumentException)
                {
                    failures.Add(new RestoreFailure(directory, Reason(ex)));
                }
            }
        }

        foreach (var file in plan.Files)
        {
            if (ct.IsCancellationRequested)
                return Result(canceled: true);

            var bytesBefore = bytesDone;
            progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, file.Destination));
            try
            {
                switch (await RestoreFileAsync().ConfigureAwait(false))
                {
                    case Outcome.Copied:
                        copied++;
                        break;
                    case Outcome.KeptBoth:
                        keptBoth++;
                        break;
                    case Outcome.Skipped:
                        skipped++;
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Result(canceled: true);
            }
            catch (Exception ex) when (ex is StorageException or ArgumentException)
            {
                // ArgumentException: a name the destination cannot hold (e.g. ':' on Windows); only this file fails.
                failures.Add(new RestoreFailure(file.Destination, Reason(ex)));
            }
            catch (RefusedException ex)
            {
                failures.Add(new RestoreFailure(file.Destination, ex.Message));
            }
            finally
            {
                filesDone++;
                bytesDone = bytesBefore + file.Size;
            }

            async Task<Outcome> RestoreFileAsync()
            {
                // Checked right before writing, from the root down: a link anywhere on the way is never written through.
                var target = file.Destination;
                if (await FindLinkAsync(destination, StoragePath.Parent(target), ct).ConfigureAwait(false))
                    throw new RefusedException(CoreTexts.English("core.restore.destinationLink"));

                var keepBoth = false;
                if (await destination.StatAsync(target, ct).ConfigureAwait(false) is { } existing)
                {
                    switch (policy)
                    {
                        case ConflictPolicy.Skip:
                            return Outcome.Skipped;
                        case ConflictPolicy.KeepBoth:
                            target = await FreeKeepBothNameAsync(destination, file.Destination, plan.VersionTime, ct).ConfigureAwait(false);
                            keepBoth = true;
                            break;
                        default:
                            if (existing.IsLink)
                                throw new RefusedException(CoreTexts.English("core.restore.destinationLink"));
                            if (existing.IsDirectory)
                                throw new RefusedException(CoreTexts.English("core.restore.folderExists"));
                            break;
                    }
                }

                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        bytesDone = bytesBefore;
                        await CopyAsync(plan.Versions, file, destination, target, policy == ConflictPolicy.Overwrite, buffer, count =>
                        {
                            bytesDone += count;
                            progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, file.Destination));
                        }, ct).ConfigureAwait(false);
                        return keepBoth ? Outcome.KeptBoth : Outcome.Copied;
                    }
                    catch (StorageConflictException) when (policy == ConflictPolicy.Skip)
                    {
                        return Outcome.Skipped;   // it appeared after planning: it is someone's file, never replaced
                    }
                    catch (StorageConflictException) when (policy == ConflictPolicy.KeepBoth && attempt < KeepBothAttempts)
                    {
                        target = await FreeKeepBothNameAsync(destination, file.Destination, plan.VersionTime, ct).ConfigureAwait(false);
                        keepBoth = true;
                    }
                }
            }
        }

        progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, ""));
        return Result(canceled: false);
    }

    /// <summary>
    /// The <paramref name="number"/>th "keep both" name of <paramref name="destination"/> (a storage path):
    /// <c>name (2026_09_30-14_05).ext</c> for 1, <c>name (2026_09_30-14_05) 2.ext</c> for 2, and so on.
    /// </summary>
    public static string KeepBothName(string destination, DateTime versionTime, int number)
    {
        var name = StoragePath.Name(destination);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var stamp = versionTime.ToString("yyyy_MM_dd-HH_mm", CultureInfo.InvariantCulture);
        var counter = number > 1 ? $" {number}" : "";
        return StoragePath.Combine(StoragePath.Parent(destination), $"{stem} ({stamp}){counter}{extension}");
    }

    /// <summary>The first "keep both" name of <paramref name="destination"/> that nothing (file, folder or link) uses yet.</summary>
    private static async Task<string> FreeKeepBothNameAsync(IStorage storage, string destination, DateTime versionTime,
        CancellationToken ct)
    {
        for (var number = 1; ; number++)
        {
            var candidate = KeepBothName(destination, versionTime, number);
            if (await storage.StatAsync(candidate, ct).ConfigureAwait(false) is null)
                return candidate;
        }
    }

    /// <summary>
    /// Copies one file into a new file of the destination and commits it; with <paramref name="overwrite"/> false the
    /// create is exclusive (<see cref="StorageConflictException"/> when the name is taken, also at the commit). Disposing
    /// the writer without a commit discards what was written. A completely written file is committed even when
    /// cancellation comes in meanwhile.
    /// </summary>
    private static async Task CopyAsync(IStorage versions, RestoreFile file, IStorage destination, string target, bool overwrite,
        byte[] buffer, Action<int> onBytes, CancellationToken ct)
    {
        var input = await versions.OpenReadAsync(file.Source, ct).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            var output = await destination.CreateAsync(target, new CreateOptions(Overwrite: overwrite, ModifiedUtc: file.ModifiedUtc), ct)
                .ConfigureAwait(false);
            await using (output.ConfigureAwait(false))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    onBytes(read);
                }
                await output.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The entry of <paramref name="relative"/> (not "") in the version, after checking every part of the path on the
    /// way: none may be a link (they are not followed), and the entry must exist.
    /// </summary>
    private static async Task<StorageEntry> FindInVersionAsync(IStorage versions, string versionPath, string relative,
        string relativePath, CancellationToken ct)
    {
        StorageEntry? entry = null;
        var current = versionPath;
        foreach (var part in relative.Split('/'))
        {
            current = StoragePath.Combine(current, part);
            entry = await versions.StatAsync(current, ct).ConfigureAwait(false);
            if (entry is null)
                throw new ArgumentException(CoreTexts.English("core.restore.notInVersion", ("path", relativePath)), nameof(relativePath));
            if (entry.IsLink)
                throw new ArgumentException(CoreTexts.English("core.restore.throughLink", ("path", relativePath),
                    ("link", DisplayPath(versions, current))), nameof(relativePath));
        }
        return entry!;
    }

    /// <summary>
    /// True when an existing part of <paramref name="path"/> (the storage's root, every folder on the way and the path
    /// itself) is a link: nothing is written there, since it would land wherever the link points. Asks the storage one
    /// part at a time from the root down, and stops at the first part that does not exist.
    /// </summary>
    private static async Task<bool> FindLinkAsync(IStorage storage, string path, CancellationToken ct)
    {
        if (!storage.Capabilities.HasFlag(StorageCapabilities.Links))
            return false;
        var current = "";
        var parts = path.Length == 0 ? [] : path.Split('/');
        for (var i = -1; i < parts.Length; i++)
        {
            if (i >= 0)
                current = StoragePath.Combine(current, parts[i]);
            if (await storage.StatAsync(current, ct).ConfigureAwait(false) is not { } entry)
                return false;
            if (entry.IsLink)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="path"/> exists in the destination without a link on the way to it. A file behind a link
    /// (it fails in the run), or with a name the storage cannot address, is no conflict. The folders on the way are
    /// checked once per plan.
    /// </summary>
    private static async Task<bool> ExistsOutsideLinksAsync(IStorage storage, string path, Dictionary<string, bool> linkFree,
        CancellationToken ct)
    {
        try
        {
            var parent = StoragePath.Parent(path);
            if (!linkFree.TryGetValue(parent, out var free))
                linkFree[parent] = free = !await FindLinkAsync(storage, parent, ct).ConfigureAwait(false);
            return free && await storage.StatAsync(path, ct).ConfigureAwait(false) is not null;
        }
        catch (Exception ex) when (ex is StorageException or ArgumentException)
        {
            return false;   // the run reports it for this file
        }
    }

    private static async Task<List<StorageEntry>> ListAsync(IStorage storage, string folder, CancellationToken ct)
    {
        var entries = new List<StorageEntry>();
        await foreach (var entry in storage.ListAsync(folder, recursive: false, ct).ConfigureAwait(false))
            entries.Add(entry);
        return entries;
    }

    /// <summary>What the restore takes from a manifest: file times and folders (both version-relative), and the creation time.</summary>
    private sealed record ManifestData(Dictionary<string, DateTime> Mtimes, List<string> Directories, DateTime CreatedUtc);

    /// <summary>
    /// The modified times of the manifest's files and its folders, both at or below the selected paths, and the version's
    /// creation time. Null when the version has no readable manifest: the copies' own times are used then.
    /// </summary>
    private static async Task<ManifestData?> ReadManifestAsync(IStorage versions, string versionPath, List<string> selected,
        StringComparer comparer, CancellationToken ct)
    {
        var mtimes = new Dictionary<string, DateTime>(comparer);
        var directories = new List<string>();
        try
        {
            var stream = await versions.OpenReadAsync(StoragePath.Combine(versionPath, VersionName.ManifestFileName), ct)
                .ConfigureAwait(false);
            ManifestSummary summary;
            await using (stream.ConfigureAwait(false))
            {
                summary = ManifestStream.Read(stream,
                    file =>
                    {
                        if (file.MtimeUtc != default && IsSelected(file.Path))
                            mtimes[file.Path] = file.MtimeUtc;
                    },
                    folder =>
                    {
                        if (TryCheckRelative(folder) is { Length: > 0 } valid && IsSelected(valid))
                            directories.Add(valid);
                    }, ct);
            }
            return new ManifestData(mtimes, directories, summary.CreatedUtc);
        }
        catch (Exception ex) when (ex is StorageException or JsonException)
        {
            return null;
        }

        bool IsSelected(string path) => selected.Any(root => IsSameOrBelow(path, root, comparer));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies below it (<c>""</c> holds everything).</summary>
    private static bool IsSameOrBelow(string path, string root, StringComparer comparer) =>
        root.Length == 0 || comparer.Equals(path, root) ||
        (path.Length > root.Length && path[root.Length] == '/' && comparer.Equals(path[..root.Length], root));

    /// <summary>The path with forward slashes; rooted paths, drive letters and "." or ".." segments are rejected.</summary>
    private static string CheckRelative(string relativePath) =>
        TryCheckRelative(relativePath) ??
        throw new ArgumentException(CoreTexts.English("core.restore.notAPath", ("path", relativePath)), nameof(relativePath));

    private static string? TryCheckRelative(string relativePath)
    {
        var relative = relativePath.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(relativePath) || relative.Contains(':') ||
            relative.Split('/').Any(part => part is "." or ".." || (relative.Length > 0 && part.Length == 0)))
            return null;
        return relative;
    }

    private static bool IsManifest(string relative) =>
        string.Equals(relative, VersionName.ManifestFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The time in the folder name; else the manifest's creation time; else the folder's modified time (all local).</summary>
    private static DateTime VersionTimeOf(string versionPath, DateTime? createdUtc, StorageEntry versionEntry)
    {
        if (VersionName.TryParseAny(StoragePath.Name(versionPath), out var time, out _))
            return time;
        if (createdUtc is { } created && created != default)
            return (created.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(created, DateTimeKind.Utc) : created).ToLocalTime();
        return versionEntry.ModifiedUtc.ToLocalTime();
    }

    /// <summary>A path for messages: the full path on a file system, else the storage path.</summary>
    private static string DisplayPath(IStorage storage, string path)
    {
        if (storage is not FileSystemStorage folder)
            return path;
        try
        {
            return folder.FullPathOf(path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static string Reason(Exception exception) => exception switch
    {
        StorageAccessDeniedException => CoreTexts.English("core.restore.accessDenied"),
        StorageLockedException => CoreTexts.English("core.file.locked"),
        StorageException { InnerException: { } inner } => inner.Message,
        ArgumentException { ParamName: { } name } => exception.Message.Replace($" (Parameter '{name}')", "", StringComparison.Ordinal),
        _ => exception.Message,
    };

    private enum Outcome { Copied, KeptBoth, Skipped }

    /// <summary>A file the restore does not write, with the reason (a link or a folder in its place).</summary>
    private sealed class RefusedException(string message) : Exception(message);
}
