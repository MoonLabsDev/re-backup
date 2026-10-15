using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ReBackup.Core.Versions;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;

namespace ReBackup.App.Services;

/// <summary>
/// Opens a version's file or folder through a temporary copy, for versions whose files cannot be opened in place: those
/// of incremental plans (their files live in the pool) and those of full plans on targets that are no folder.
/// </summary>
public interface ITempOpener
{
    /// <summary>
    /// Restores the file <paramref name="relativePath"/> of the version <paramref name="versionName"/> (a folder in
    /// <paramref name="versions"/>, as <see cref="VersionViews.OpenAsync"/> returns it) into the temp folder, verified
    /// against the manifest, and opens it with its program. A program, script or shortcut is never started: its copy
    /// is shown selected in Explorer instead. False when nothing was opened (the copy could not be made completely, or
    /// nothing opens it).
    /// </summary>
    /// <exception cref="OperationCanceledException">Canceled.</exception>
    /// <exception cref="StorageException">The version cannot be read.</exception>
    /// <exception cref="ArgumentException">The path leaves the version or does not exist in it.</exception>
    Task<bool> OpenFileAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct);

    /// <summary>
    /// Restores the folder <paramref name="relativePath"/> ("" = the whole version) like <see cref="OpenFileAsync"/>
    /// and opens Explorer on it; false (nothing opened) unless every file was copied.
    /// </summary>
    /// <inheritdoc cref="OpenFileAsync" path="/exception"/>
    Task<bool> OpenFolderAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct);

    /// <summary>Deletes the temp folder's entries last written more than <paramref name="olderThan"/> ago; what is in use stays.</summary>
    void CleanUp(TimeSpan olderThan);
}

/// <summary>
/// <see cref="ITempOpener"/> below <c>%TEMP%\ReBackup\open\&lt;version&gt;\</c>: a file lands there under its name, a
/// folder with its name, the whole version as that folder itself. Copies are restored with the <see cref="Restorer"/>
/// (into a folder, replacing an earlier copy), which checks every file against its hash.
/// </summary>
public sealed class TempOpener : ITempOpener
{
    private readonly Func<ProcessStartInfo, bool> _start;

    /// <param name="root">The temp folder; <see cref="DefaultRoot"/> when null.</param>
    /// <param name="start">Starts a process (the shell or Explorer) and says whether it started; tests record instead.</param>
    public TempOpener(string? root = null, Func<ProcessStartInfo, bool>? start = null)
    {
        Root = root ?? DefaultRoot;
        _start = start ?? Start;
    }

    /// <summary><c>%TEMP%\ReBackup\open</c>.</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "ReBackup", "open");

    public string Root { get; }

    private static string Explorer =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public async Task<bool> OpenFileAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct)
    {
        if (await RestoreAsync(versions, versionName, relativePath, ct).ConfigureAwait(false) is not { } path ||
            !File.Exists(path))
            return false;
        return RunnableFiles.IsRunnable(Path.GetExtension(path))
            ? _start(new ProcessStartInfo(Explorer, $"/select,\"{path}\"") { UseShellExecute = false })
            : _start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public async Task<bool> OpenFolderAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct)
    {
        if (await RestoreAsync(versions, versionName, relativePath, ct).ConfigureAwait(false) is not { } path ||
            !Directory.Exists(path))
            return false;
        return _start(new ProcessStartInfo(Explorer, $"\"{path}\"") { UseShellExecute = false });
    }

    public void CleanUp(TimeSpan olderThan)
    {
        if (!Directory.Exists(Root))
            return;
        var limit = DateTime.UtcNow - olderThan;
        try
        {
            foreach (var entry in new DirectoryInfo(Root).EnumerateFileSystemInfos())
            {
                try
                {
                    if (entry.LastWriteTimeUtc >= limit)
                        continue;
                    if (entry is DirectoryInfo folder)
                        folder.Delete(recursive: true);
                    else
                        entry.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Still open in a program: the next start tries again.
                    Trace.TraceWarning($"The temporary copy \"{entry.FullName}\" could not be deleted: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"The temporary folder \"{Root}\" could not be cleaned up: {ex.Message}");
        }
    }

    /// <summary>
    /// Restores the entry and returns its local path; null when the version name is no folder name or a file could not
    /// be copied (e.g. it does not match its hash).
    /// </summary>
    private async Task<string?> RestoreAsync(IStorage versions, string versionName, string relativePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(versions);
        if (FolderName(versionName) is not { } name)
            return null;
        var folder = Path.Combine(Root, name);
        var relative = relativePath.Replace('\\', '/').Trim('/');
        // The whole version goes into the temp root under its own name; anything else into the version's folder.
        var destinationRoot = relative.Length == 0 ? Root : folder;
        Directory.CreateDirectory(destinationRoot);
        var plan = await Restorer.PlanAsync(versions, versionName, [relativePath], new FileSystemStorage(destinationRoot),
            RestoreMode.ToFolder, ct).ConfigureAwait(false);
        var result = await Restorer.RunAsync(plan, ConflictPolicy.Overwrite, null, ct).ConfigureAwait(false);
        // The version's folder counts as used now (its copies keep the version's times): the clean-up keeps it a day.
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        ct.ThrowIfCancellationRequested();
        if (result.Canceled)
            throw new OperationCanceledException(ct);
        if (result.Failures.Count > 0)
        {
            Trace.TraceWarning($"\"{relativePath}\" of \"{versionName}\" was not opened: " +
                               string.Join("; ", result.Failures.Select(f => $"{f.Path}: {f.Reason}")));
            return null;
        }
        return relative.Length == 0 ? folder : Path.Combine(folder, relative[(relative.LastIndexOf('/') + 1)..]);
    }

    /// <summary>The version folder's own name (the last part of its path); null when it cannot be a local folder name.</summary>
    private static string? FolderName(string versionName)
    {
        var name = versionName.Replace('\\', '/').TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? null
            : name;
    }

    private static bool Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
