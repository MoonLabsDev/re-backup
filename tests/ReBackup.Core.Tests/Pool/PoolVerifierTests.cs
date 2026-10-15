using FluentAssertions;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage.InMemory;
using static ReBackup.Core.Tests.TestSupport.PoolVersions;

namespace ReBackup.Core.Tests.Pool;

/// <summary>Verify backup (spec 9): quick presence check, thorough content check, never a write.</summary>
public class PoolVerifierTests
{
    private const string PlanId = "p1";
    private const string PlanName = "Projects";
    private const string V1 = "2026_10_15-10_00 Projects";
    private const string V2 = "2026_10_16-10_00 Projects";

    private readonly InMemoryStorage _target = new();

    private void Version(string name, params (string Path, string Content)[] files) =>
        PoolVersions.Write(_target, name, PlanId, PlanName, files);

    private Task<IReadOnlyList<PoolProblem>> Verify(bool thorough, Storage.IStorage? target = null,
        IProgress<VerifyProgress>? progress = null) =>
        PoolVerifier.VerifyAsync(target ?? _target, PlanId, PlanName, thorough, progress, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Healthy_pool_has_no_problems(bool thorough)
    {
        Version(V1, ("a.txt", "one"), ("b.txt", "two"));
        Version(V2, ("a.txt", "one"));

        (await Verify(thorough)).Should().BeEmpty();
    }

    [Fact]
    public async Task Missing_blob_reported_for_every_version_using_it()
    {
        Version(V1, ("a.txt", "shared"), ("b.txt", "fine"));
        Version(V2, ("copy/a.txt", "shared"));
        await _target.DeleteAsync([BlobOf("shared")], CancellationToken.None);

        var expected = new[]
        {
            new PoolProblem(V1, "a.txt", HashOf("shared"), PoolProblemKind.Missing),
            new PoolProblem(V2, "copy/a.txt", HashOf("shared"), PoolProblemKind.Missing),
        };
        (await Verify(false)).Should().Equal(expected);
        (await Verify(true)).Should().Equal(expected);
    }

    [Fact]
    public async Task Corrupt_blob_found_only_when_thorough()
    {
        Version(V1, ("a.txt", "shared"));
        Version(V2, ("b.txt", "shared"));
        _target.AddFile(BlobOf("shared"), Bytes("bit rot"));

        (await Verify(false)).Should().BeEmpty();
        (await Verify(true)).Should().Equal(
            new PoolProblem(V1, "a.txt", HashOf("shared"), PoolProblemKind.Corrupt),
            new PoolProblem(V2, "b.txt", HashOf("shared"), PoolProblemKind.Corrupt));
    }

    [Fact]
    public async Task Thorough_reads_each_blob_once_and_compares_hashes_in_lower_case()
    {
        PoolVersions.Write(_target, V1, PlanId, PlanName, [("a.txt", "shared")], hash => hash.ToUpperInvariant());
        Version(V2, ("b.txt", "shared"));
        var wrapped = new WrappedStorage(_target);

        (await Verify(true, wrapped)).Should().BeEmpty();

        // two manifests and the one shared blob
        wrapped.Opens.Should().Be(3);
    }

    [Fact]
    public async Task Unreadable_manifest_reported()
    {
        Version(V1, ("a.txt", "one"));
        _target.AddFile($"{V2}/re-manifest.json", Bytes("{ not a manifest"));

        (await Verify(true)).Should().Equal(
            new PoolProblem(V2, "re-manifest.json", "", PoolProblemKind.ManifestUnreadable));
    }

    [Fact]
    public async Task Folder_without_manifest_is_not_a_version()
    {
        Version(V1, ("a.txt", "one"));
        _target.AddFile("notes/readme.txt", Bytes("hi"));

        (await Verify(true)).Should().BeEmpty();
    }

    [Fact]
    public async Task Pool_missing_entirely_reports_every_file()
    {
        Version(V1, ("a.txt", "one"));
        await _target.DeleteAsync(Blobs(_target), CancellationToken.None);

        (await Verify(false)).Should().Equal(new PoolProblem(V1, "a.txt", HashOf("one"), PoolProblemKind.Missing));
    }

    [Fact]
    public async Task Progress_counts_versions_then_blobs()
    {
        Version(V1, ("a.txt", "one"));
        Version(V2, ("b.txt", "two"));
        var reports = new List<VerifyProgress>();

        await Verify(true, progress: new SyncProgress<VerifyProgress>(reports.Add));

        reports.Should().Contain(new VerifyProgress(2, 2, ""));
        reports.Count(r => r.Current.StartsWith("re-pool/", StringComparison.Ordinal)).Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verify_never_writes(bool thorough)
    {
        Version(V1, ("a.txt", "one"));
        Version(V2, ("a.txt", "one"), ("b.txt", "gone"));
        await _target.DeleteAsync([BlobOf("gone")], CancellationToken.None);
        _target.AddFile(BlobOf("one"), Bytes("rotten"));
        var wrapped = new WrappedStorage(_target);

        await Verify(thorough, wrapped);

        wrapped.Creates.Should().Be(0);
        wrapped.Deletes.Should().Be(0);
    }
}
