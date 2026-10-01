using System.Diagnostics;
using ReBackup.Core.Ignore;
using ReBackup.Core.IO;
using ReBackup.Core.Localization;

namespace ReBackup.Core.Indexing;

public sealed class IndexNode
{
    public required string Name { get; init; }

    /// <summary>Path relative to the source root, with forward slashes; "" for the root.</summary>
    public required string RelativePath { get; init; }

    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public IReadOnlyList<IndexNode> Children { get; init; } = [];

    /// <summary>Why this folder could not be (fully) read, if so.</summary>
    public string? Error { get; init; }
}

/// <summary>One scan of a source folder. <see cref="DirectoryCount"/> does not include the root.</summary>
public sealed record SourceIndex(
    string Root,
    IndexNode RootNode,
    IReadOnlyList<NestedIgnoreFile> IgnoreFiles,
    int FileCount,
    int DirectoryCount)
{
    /// <summary>Relative paths of <c>.backupignore</c> files that could not be read; their patterns are not applied.</summary>
    public IReadOnlyList<string> UnreadableIgnoreFiles { get; init; } = [];
}

public readonly record struct IndexProgress(int Files, int Directories, string CurrentDirectory);

public static class SourceIndexer
{
    /// <summary>Folders nested deeper than this are not scanned.</summary>
    public const int MaxDepth = 256;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public static Task<SourceIndex> BuildAsync(string root, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(root, progress, cancellationToken), cancellationToken);

    public static SourceIndex Build(string root, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullRoot = PathUtil.Normalize(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException(CoreTexts.English("core.run.sourceMissing", ("source", fullRoot)));

        var walk = new Walk(progress, cancellationToken);
        var name = Path.GetFileName(fullRoot);
        var rootNode = walk.ScanDirectory(new DirectoryInfo(fullRoot), name.Length > 0 ? name : fullRoot, "", 0);
        progress?.Report(new IndexProgress(walk.Files, walk.Directories, fullRoot));
        return new SourceIndex(fullRoot, rootNode, walk.IgnoreFiles, walk.Files, walk.Directories)
        {
            UnreadableIgnoreFiles = walk.UnreadableIgnoreFiles,
        };
    }

    private sealed class Walk(IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();

        public List<NestedIgnoreFile> IgnoreFiles { get; } = [];
        public List<string> UnreadableIgnoreFiles { get; } = [];
        public int Files { get; private set; }
        public int Directories { get; private set; }

        public IndexNode ScanDirectory(DirectoryInfo directory, string name, string relativePath, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportThrottled(directory.FullName);

            var children = new List<IndexNode>();
            string? error = null;
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var childPath = relativePath.Length == 0 ? entry.Name : relativePath + "/" + entry.Name;

                    if (entry is DirectoryInfo subdirectory)
                    {
                        Directories++;
                        if (subdirectory.LinkTarget is not null)
                        {
                            children.Add(new IndexNode
                            {
                                Name = subdirectory.Name,
                                RelativePath = childPath,
                                IsDirectory = true,
                                LastWriteUtc = subdirectory.LastWriteTimeUtc,
                                Error = CoreTexts.English("core.scan.link"),
                            });
                        }
                        else if (depth + 1 > SourceIndexer.MaxDepth)
                        {
                            children.Add(new IndexNode
                            {
                                Name = subdirectory.Name,
                                RelativePath = childPath,
                                IsDirectory = true,
                                LastWriteUtc = subdirectory.LastWriteTimeUtc,
                                Error = CoreTexts.English("core.scan.tooDeep"),
                            });
                        }
                        else
                        {
                            children.Add(ScanDirectory(subdirectory, subdirectory.Name, childPath, depth + 1));
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        Files++;
                        children.Add(new IndexNode
                        {
                            Name = file.Name,
                            RelativePath = childPath,
                            IsDirectory = false,
                            Size = file.Length,
                            LastWriteUtc = file.LastWriteTimeUtc,
                        });
                        if (file.Name.Equals(IgnoreOrigins.NestedFileName, StringComparison.OrdinalIgnoreCase))
                            ReadIgnoreFile(file, relativePath);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                error = ex.Message;
            }

            children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return new IndexNode
            {
                Name = name,
                RelativePath = relativePath,
                IsDirectory = true,
                LastWriteUtc = directory.LastWriteTimeUtc,
                Children = children,
                Error = error,
            };
        }

        private void ReadIgnoreFile(FileInfo file, string directoryRelativePath)
        {
            try
            {
                IgnoreFiles.Add(new NestedIgnoreFile(directoryRelativePath, File.ReadAllLines(file.FullName)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                UnreadableIgnoreFiles.Add(IgnoreOrigins.ForNestedFile(directoryRelativePath));
            }
        }

        private void ReportThrottled(string currentDirectory)
        {
            if (progress is null || _sinceReport.Elapsed < ProgressInterval)
                return;
            _sinceReport.Restart();
            progress.Report(new IndexProgress(Files, Directories, currentDirectory));
        }
    }
}
