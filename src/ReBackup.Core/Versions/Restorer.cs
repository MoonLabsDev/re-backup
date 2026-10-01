using System.Globalization;
using ReBackup.Core.Backup;
using ReBackup.Core.IO;

namespace ReBackup.Core.Versions;

/// <summary>Original: back to where the version came from. ToFolder: into a chosen folder, under the item's name.</summary>
public enum RestoreMode { Original, ToFolder }

/// <summary>What happens to a file that already exists at the destination; asked once per restore.</summary>
public enum ConflictPolicy { Overwrite, Skip, KeepBoth }

/// <summary>One file to copy: its path in the version folder and where it goes.</summary>
public sealed record RestoreFile(string Source, string Destination, long Size);

/// <summary>Everything a restore will do; made by <see cref="Restorer.Plan"/>, nothing is written yet.</summary>
public sealed class RestorePlan
{
    public required string VersionFolder { get; init; }
    public required string DestinationRoot { get; init; }

    /// <summary>The version's time: the stamp of "keep both" names.</summary>
    public required DateTime VersionTime { get; init; }

    /// <summary>Folders to create (the selected folders and everything below them, also empty ones).</summary>
    public required IReadOnlyList<string> Directories { get; init; }

    public required IReadOnlyList<RestoreFile> Files { get; init; }

    /// <summary>Files whose destination exists already.</summary>
    public required IReadOnlyList<RestoreFile> Conflicts { get; init; }

    public long TotalBytes => Files.Sum(f => f.Size);
}

public sealed record RestoreFailure(string Path, string Reason);

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
/// it replaces is a conflicting one under <see cref="ConflictPolicy.Overwrite"/>.
/// </summary>
public static class Restorer
{
    public const string TempSuffix = ".rebackup-tmp";
    private const int BufferSize = 1024 * 1024;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// Lists what restoring <paramref name="relativePaths"/> (files or folders of the version, forward or back slashes,
    /// "" = the whole version) to <paramref name="destinationRoot"/> copies, and which files exist there already.
    /// Original: destination = root + relative path. ToFolder: destination = root + the item's name.
    /// </summary>
    /// <exception cref="ArgumentException">A path leaves the version or the destination, does not exist, or is a link.</exception>
    /// <exception cref="DirectoryNotFoundException">The version folder does not exist.</exception>
    public static RestorePlan Plan(string versionFolder, IReadOnlyList<string> relativePaths, string destinationRoot,
        RestoreMode mode)
    {
        if (!Path.IsPathFullyQualified(destinationRoot))
            throw new ArgumentException("The destination must be an absolute path.", nameof(destinationRoot));
        versionFolder = PathUtil.Normalize(versionFolder);
        destinationRoot = PathUtil.Normalize(destinationRoot);
        if (!Directory.Exists(versionFolder))
            throw new DirectoryNotFoundException($"The version folder \"{versionFolder}\" does not exist.");

        var directories = new List<string>();
        var files = new List<RestoreFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relativePath in relativePaths)
        {
            var relative = CheckRelative(relativePath);
            var source = relative.Length == 0 ? versionFolder : Path.GetFullPath(Path.Combine(versionFolder, relative));
            if (!PathUtil.IsSameOrInside(source, versionFolder))
                throw new ArgumentException($"\"{relativePath}\" is outside the version.", nameof(relativePaths));
            var name = relative.Length == 0 ? Path.GetFileName(versionFolder) : Path.GetFileName(relative);
            var destination = mode == RestoreMode.Original
                ? (relative.Length == 0 ? destinationRoot : Path.Combine(destinationRoot, relative))
                : Path.Combine(destinationRoot, name);

            if (File.Exists(source))
            {
                if (IsLink(source))
                    throw new ArgumentException($"\"{relativePath}\" is a link.", nameof(relativePaths));
                if (IsManifest(source, versionFolder))
                    throw new ArgumentException("The manifest is not part of the backup.", nameof(relativePaths));
                AddFile(files, seen, source, destination, new FileInfo(source).Length, destinationRoot);
            }
            else if (Directory.Exists(source))
            {
                if (IsLink(source))
                    throw new ArgumentException($"\"{relativePath}\" is a link.", nameof(relativePaths));
                AddFolder(directories, files, seen, source, destination, versionFolder, destinationRoot);
            }
            else
            {
                throw new ArgumentException($"\"{relativePath}\" does not exist in the version.", nameof(relativePaths));
            }
        }

        return new RestorePlan
        {
            VersionFolder = versionFolder,
            DestinationRoot = destinationRoot,
            VersionTime = VersionTimeOf(versionFolder),
            Directories = directories,
            Files = files,
            Conflicts = files.Where(f => File.Exists(f.Destination) || Directory.Exists(f.Destination)).ToList(),
        };
    }

    /// <summary>
    /// Copies the plan's files. Each file is written to <c>&lt;name&gt;.rebackup-tmp</c> next to its target first and
    /// then renamed; it keeps the version's last write time. A file that cannot be written is recorded as a failure
    /// and the rest continues. Cancellation stops before the next file (a half-written temp file is removed).
    /// </summary>
    public static RestoreResult Run(RestorePlan plan, ConflictPolicy policy, IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<RestoreFailure>();
        int copied = 0, skipped = 0, keptBoth = 0, filesDone = 0;
        long bytesDone = 0;
        var bytesTotal = plan.TotalBytes;
        var buffer = new byte[BufferSize];
        RestoreResult Result(bool canceled) => new(copied, skipped, keptBoth, failures, canceled);

        foreach (var directory in plan.Directories)
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new RestoreFailure(directory, Reason(ex)));
            }
        }

        foreach (var file in plan.Files)
        {
            if (cancellationToken.IsCancellationRequested)
                return Result(canceled: true);

            var bytesBefore = bytesDone;
            progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, file.Destination));
            try
            {
                var target = file.Destination;
                var overwrite = false;
                var keepBoth = false;
                if (File.Exists(target) || Directory.Exists(target))
                {
                    switch (policy)
                    {
                        case ConflictPolicy.Skip:
                            skipped++;
                            continue;
                        case ConflictPolicy.KeepBoth:
                            target = KeepBothName(target, plan.VersionTime);
                            keepBoth = true;
                            break;
                        default:
                            if (Directory.Exists(target))
                                throw new IOException("A folder with this name exists.");
                            overwrite = true;
                            break;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + TempSuffix;
                if (!CopyToTemp(file.Source, temp, buffer, count =>
                    {
                        bytesDone += count;
                        progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, file.Destination));
                    }, cancellationToken))
                {
                    return Result(canceled: true);
                }

                try
                {
                    File.Move(temp, target, overwrite);
                }
                catch
                {
                    TryDelete(temp);
                    throw;
                }

                if (keepBoth)
                    keptBoth++;
                else
                    copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new RestoreFailure(file.Destination, Reason(ex)));
            }
            finally
            {
                filesDone++;
                bytesDone = bytesBefore + file.Size;
            }
        }

        progress?.Report(new RestoreProgress(filesDone, plan.Files.Count, bytesDone, bytesTotal, ""));
        return Result(canceled: false);
    }

    /// <summary>
    /// <c>name (2026_09_30-14_05).ext</c> next to <paramref name="destination"/>; <c>name (2026_09_30-14_05) 2.ext</c>,
    /// <c>… 3.ext</c> … when that exists too.
    /// </summary>
    public static string KeepBothName(string destination, DateTime versionTime)
    {
        var folder = Path.GetDirectoryName(destination)!;
        var stem = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);
        var stamp = versionTime.ToString("yyyy_MM_dd-HH_mm", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(folder, $"{stem} ({stamp}){extension}");
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(folder, $"{stem} ({stamp}) {n}{extension}");
        return candidate;
    }

    /// <summary>False when canceled (the temp file is removed then).</summary>
    private static bool CopyToTemp(string source, string temp, byte[] buffer, Action<int> onBytes,
        CancellationToken cancellationToken)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                   BufferSize, FileOptions.SequentialScan))
        using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    output.Dispose();
                    TryDelete(temp);
                    return false;
                }
                output.Write(buffer, 0, read);
                onBytes(read);
            }
        }

        File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(source));
        return true;
    }

    private static void AddFolder(List<string> directories, List<RestoreFile> files, HashSet<string> seen,
        string sourceFolder, string destinationFolder, string versionFolder, string destinationRoot)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((sourceFolder, destinationFolder));
        while (pending.Count > 0)
        {
            var (source, destination) = pending.Pop();
            CheckInside(destination, destinationRoot);
            directories.Add(destination);
            foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;   // links are not followed
                var target = Path.Combine(destination, entry.Name);
                if (entry is DirectoryInfo)
                    pending.Push((entry.FullName, target));
                else if (entry is FileInfo file && !IsManifest(file.FullName, versionFolder))
                    AddFile(files, seen, file.FullName, target, file.Length, destinationRoot);
            }
        }
    }

    private static void AddFile(List<RestoreFile> files, HashSet<string> seen, string source, string destination,
        long size, string destinationRoot)
    {
        CheckInside(destination, destinationRoot);
        if (seen.Add(destination))
            files.Add(new RestoreFile(source, destination, size));
    }

    private static void CheckInside(string destination, string destinationRoot)
    {
        if (!PathUtil.IsSameOrInside(destination, destinationRoot))
            throw new ArgumentException($"\"{destination}\" is outside the destination.");
    }

    /// <summary>The path with back slashes; rooted paths, drive letters and "." or ".." segments are rejected.</summary>
    private static string CheckRelative(string relativePath)
    {
        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath) || relative.Contains(Path.VolumeSeparatorChar) ||
            relative.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".." || (relative.Length > 0 && part.Length == 0)))
            throw new ArgumentException($"\"{relativePath}\" is not a path inside the version.", nameof(relativePath));
        return relative;
    }

    private static bool IsManifest(string path, string versionFolder) =>
        string.Equals(path, Path.Combine(versionFolder, VersionName.ManifestFileName), StringComparison.OrdinalIgnoreCase);

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static DateTime VersionTimeOf(string versionFolder) =>
        VersionName.TryParseAny(Path.GetFileName(versionFolder), out var time, out _)
            ? time
            : Directory.GetLastWriteTime(versionFolder);

    private static string Reason(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access denied (read-only, or in use by another program)",
        IOException io when (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation => "locked by another program",
        _ => exception.Message,
    };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
