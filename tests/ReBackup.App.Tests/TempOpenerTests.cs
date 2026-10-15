using System.Diagnostics;
using System.IO;
using System.Text;
using FluentAssertions;
using ReBackup.App.Services;
using ReBackup.App.Tests.TestSupport;
using ReBackup.Core.Pool;
using ReBackup.Storage.InMemory;

namespace ReBackup.App.Tests;

/// <summary>
/// Opening a version's file or folder through a temporary copy: restored (and verified against the manifest's hashes)
/// below the temp root, then handed to the shell — here a recording starter, so no process is ever started.
/// </summary>
public class TempOpenerTests : IDisposable
{
    private const string Version = "2026_09_29-14_05 Projects";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rebackup-app-tests", Guid.NewGuid().ToString("N"), "open");
    private readonly List<ProcessStartInfo> _started = [];
    private readonly InMemoryStorage _target = new();
    private readonly TempOpener _opener;

    public TempOpenerTests()
    {
        _opener = new TempOpener(_root, info =>
        {
            _started.Add(info);
            return true;
        });
        PoolTargets.WriteStore(_target, "p1");
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private PoolVersionView View => new(_target, Version);

    [Fact]
    public async Task Restores_and_verifies_before_opening()
    {
        PoolTargets.WriteVersion(_target, Version, "p1", "Projects",
            new PoolFile("docs/a.txt", "alpha"), new PoolFile("docs/sub/b.txt", "beta"), new PoolFile("bad.txt", "good"));
        // The blob of bad.txt no longer holds what the manifest's hash says.
        _target.AddFile(PoolLayout.BlobPath(PoolTargets.HashOf("good")), Encoding.UTF8.GetBytes("evil"));

        (await _opener.OpenFileAsync(View, Version, "docs/a.txt", CancellationToken.None)).Should().BeTrue();

        var file = Path.Combine(_root, Version, "a.txt");
        File.ReadAllText(file).Should().Be("alpha");
        var start = _started.Should().ContainSingle().Subject;
        start.FileName.Should().Be(file);
        start.UseShellExecute.Should().BeTrue();

        (await _opener.OpenFileAsync(View, Version, "bad.txt", CancellationToken.None)).Should().BeFalse();
        _started.Should().HaveCount(1, "a file that does not match its hash is never opened");
        File.Exists(Path.Combine(_root, Version, "bad.txt")).Should().BeFalse();

        (await _opener.OpenFolderAsync(View, Version, "docs", CancellationToken.None)).Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, Version, "docs", "sub", "b.txt")).Should().Be("beta");
        var explorer = _started.Should().HaveCount(2).And.Subject.Last();
        explorer.FileName.Should().EndWith("explorer.exe");
        explorer.Arguments.Should().Be($"\"{Path.Combine(_root, Version, "docs")}\"");
    }

    [Fact]
    public async Task Whole_version_lands_in_its_own_folder()
    {
        PoolTargets.WriteVersion(_target, Version, "p1", "Projects", new PoolFile("docs/a.txt", "alpha"));

        (await _opener.OpenFolderAsync(View, Version, "", CancellationToken.None)).Should().BeTrue();

        File.ReadAllText(Path.Combine(_root, Version, "docs", "a.txt")).Should().Be("alpha");
        _started.Should().ContainSingle().Which.Arguments.Should().Be($"\"{Path.Combine(_root, Version)}\"");
    }

    [Fact]
    public async Task A_program_is_shown_in_explorer_and_never_started()
    {
        PoolTargets.WriteVersion(_target, Version, "p1", "Projects", new PoolFile("tools/setup.exe", "MZ"));

        (await _opener.OpenFileAsync(View, Version, "tools/setup.exe", CancellationToken.None)).Should().BeTrue();

        var start = _started.Should().ContainSingle().Subject;
        start.FileName.Should().EndWith("explorer.exe");
        start.Arguments.Should().Be($"/select,\"{Path.Combine(_root, Version, "setup.exe")}\"");
    }

    [Fact]
    public async Task A_version_name_that_is_no_folder_name_is_refused()
    {
        PoolTargets.WriteVersion(_target, Version, "p1", "Projects", new PoolFile("a.txt", "alpha"));

        (await _opener.OpenFileAsync(_target, "..", "a.txt", CancellationToken.None)).Should().BeFalse();

        _started.Should().BeEmpty();
    }

    [Fact]
    public void CleanUp_removes_only_old_entries()
    {
        var old = Directory.CreateDirectory(Path.Combine(_root, "old version"));
        File.WriteAllText(Path.Combine(old.FullName, "a.txt"), "a");
        var recent = Directory.CreateDirectory(Path.Combine(_root, "recent version"));
        File.WriteAllText(Path.Combine(recent.FullName, "b.txt"), "b");
        var oldFile = Path.Combine(_root, "stray.txt");
        File.WriteAllText(oldFile, "c");
        Directory.SetLastWriteTimeUtc(old.FullName, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-2));

        _opener.CleanUp(TimeSpan.FromDays(1));

        Directory.Exists(old.FullName).Should().BeFalse();
        File.Exists(oldFile).Should().BeFalse();
        File.Exists(Path.Combine(recent.FullName, "b.txt")).Should().BeTrue();
        _started.Should().BeEmpty();
    }

    [Fact]
    public void CleanUp_without_a_temp_folder_does_nothing()
    {
        var act = () => _opener.CleanUp(TimeSpan.FromDays(1));

        act.Should().NotThrow();
        Directory.Exists(_root).Should().BeFalse();
    }
}
