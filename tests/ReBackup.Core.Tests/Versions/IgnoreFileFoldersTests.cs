using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using static ReBackup.Core.Tests.TestSupport.VersionBuilder;

namespace ReBackup.Core.Tests.Versions;

public class IgnoreFileFoldersTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;
    private readonly VersionIndex _index;

    public IgnoreFileFoldersTests()
    {
        _target = _tmp.CreateDir("target");
        _index = VersionIndex.Open(_tmp.PathOf("index.db"));
    }

    public void Dispose() => _tmp.Dispose();

    private long Folder(string path) =>
        _index.FindPath(path, isDirectory: true) ?? throw new InvalidOperationException($"{path} is not indexed");

    [Fact]
    public async Task FoldersWithIgnoreFile_returns_parent_ids_of_the_version_only()
    {
        Write(_target, "2026_09_29-14_05", [File("keep.txt", "k"), File("old/.backupignore", "*.tmp"), File("old/a.txt", "a")]);
        Write(_target, "2026_09_30-14_05",
        [
            File(".backupignore", "x"), File("sub/.BackupIgnore", "y"), File("sub/deep/f.txt", "f"),
            File("other/.backupignore.bak", "z"), File("docs/a.txt", "a"),
        ]);
        _index.Sync(await ListAsync(_target), TargetStorage(_target));
        var versions = _index.Versions();

        var newer = _index.FoldersWithIgnoreFile(versions[1].Id);
        var older = _index.FoldersWithIgnoreFile(versions[0].Id);

        newer.Should().BeEquivalentTo(new[] { Folder(""), Folder("sub") }, "the root and sub hold one; the name is case-insensitive and exact");
        older.Should().BeEquivalentTo(new[] { Folder("old") });
        _index.FoldersWithIgnoreFile(-1).Should().BeEmpty();
        IgnoreOrigins.IsNestedFileName(".BACKUPIGNORE").Should().BeTrue();
        IgnoreOrigins.IsNestedFileName(".backupignore.bak").Should().BeFalse();
    }
}
