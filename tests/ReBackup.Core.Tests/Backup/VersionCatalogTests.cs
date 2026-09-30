using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Backup;

public class VersionCatalogTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;

    public VersionCatalogTests() => _target = _tmp.CreateDir("target");

    public void Dispose() => _tmp.Dispose();

    private IReadOnlyList<VersionInfo> List() => VersionCatalog.List(_target, "p1", "Projects");

    [Fact]
    public void Lists_the_versions_of_the_plan_oldest_first_with_their_totals()
    {
        var newer = VersionFolder.Create(_target, "2026_09_30-14_05 Projects", "p1", bytes: 30);
        VersionFolder.Create(_target, "2026_09_28-02_00 Projects", "p1", bytes: 10);

        var versions = List();

        versions.Select(v => v.Name).Should().Equal("2026_09_28-02_00 Projects", "2026_09_30-14_05 Projects");
        versions.Should().OnlyContain(v => v.IsOwned && v.Ownership == VersionOwnership.Owned);
        versions[0].LocalTime.Should().Be(new DateTime(2026, 9, 28, 2, 0, 0));
        versions[1].Path.Should().Be(newer);
        versions.Select(v => v.TotalBytes).Should().Equal(10L, 30L);
        versions.Select(v => v.FileCount).Should().Equal(1, 1);
    }

    [Fact]
    public void Versions_made_under_an_earlier_plan_name_stay_with_the_plan()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Old name", "p1");

        List().Should().ContainSingle().Which.Ownership.Should().Be(VersionOwnership.Owned);
    }

    [Fact]
    public void Folders_named_like_the_plan_but_not_owned_are_listed_as_such()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "other");
        _tmp.WriteFile(@"target\2026_09_02-02_00 projects\a.txt", "x");
        _tmp.WriteFile(@"target\2026_09_03-02_00 Projects\re-manifest.json", "not json");

        var versions = List();

        versions.Select(v => v.Ownership).Should().Equal(
            VersionOwnership.Foreign, VersionOwnership.NoManifest, VersionOwnership.Unreadable);
        versions.Should().OnlyContain(v => !v.IsOwned && v.TotalBytes == null && v.FileCount == null);
    }

    [Fact]
    public void Other_folders_are_not_listed()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Other", "other");
        VersionFolder.Create(_target, "2026_09_02-02_00 Projects.partial", "p1");
        VersionFolder.Create(_target, "2026_09_03-02_00 Projects.deleting", "p1");
        VersionFolder.Create(_target, "2026_13_04-02_00 Projects", "p1");
        VersionFolder.Create(_target, "notes", "p1");
        _tmp.WriteFile(@"target\2026_09_05-02_00 Projects", "a file, not a folder");

        List().Should().BeEmpty();
    }

    [Fact]
    public void Totals_of_a_manifest_without_them_are_summed_up_from_its_file_list()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1", bytes: 25, withTotals: false);

        var version = List().Single();

        version.TotalBytes.Should().Be(25);
        version.FileCount.Should().Be(1);
    }

    [Fact]
    public void A_missing_target_has_no_versions()
    {
        VersionCatalog.List(_tmp.PathOf("nowhere"), "p1", "Projects").Should().BeEmpty();
        VersionCatalog.List("", "p1", "Projects").Should().BeEmpty();
    }

    [Fact]
    public void An_empty_plan_id_owns_nothing()
    {
        _tmp.WriteFile(@"target\2026_09_01-02_00 Projects\re-manifest.json", "{}");

        VersionCatalog.List(_target, "", "Projects").Should().ContainSingle()
            .Which.Ownership.Should().Be(VersionOwnership.Foreign);
    }

    [Fact]
    public void Probe_tells_whose_version_a_folder_is()
    {
        var own = VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1", bytes: 7);
        var empty = _tmp.CreateDir(@"target\empty");

        var (ownership, header) = VersionCatalog.Probe(own, "P1");
        ownership.Should().Be(VersionOwnership.Owned, "plan ids are compared without regard to case");
        header!.TotalBytes.Should().Be(7);

        VersionCatalog.Probe(own, "p2").Should().Be((VersionOwnership.Foreign, (ManifestHeader?)null));
        VersionCatalog.Probe(empty, "p1").Should().Be((VersionOwnership.NoManifest, (ManifestHeader?)null));
        VersionCatalog.Probe(_tmp.PathOf("nowhere"), "p1").Should().Be((VersionOwnership.NoManifest, (ManifestHeader?)null));
    }

    [Fact]
    public void A_canceled_token_stops_the_listing()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => VersionCatalog.List(_target, "p1", "Projects", cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void A_folder_that_is_a_link_is_not_listed()
    {
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), "2026_09_01-02_00 Projects", "p1");
        var link = Path.Combine(_target, "2026_09_01-02_00 Projects");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
               {
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   RedirectStandardOutput = true,
                   RedirectStandardError = true,
               })!)
        {
            process.WaitForExit();
            process.ExitCode.Should().Be(0, "the junction must exist for this test");
        }

        try
        {
            List().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(link);   // removes the junction only
        }
    }
}
