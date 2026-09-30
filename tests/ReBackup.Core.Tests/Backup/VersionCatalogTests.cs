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
    public void A_version_is_owned_whatever_the_case_of_its_name()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 PROJECTS", "p1", manifestPlanName: "projects");

        List().Should().ContainSingle().Which.Ownership.Should().Be(VersionOwnership.Owned);
    }

    [Theory]
    [InlineData("2026_09_01-02_00 Projects - Copy")]   // an Explorer copy
    [InlineData("2026_09_01-02_00 Projects KEEP")]     // renamed to protect it
    [InlineData("2026_09_01-02_00 Project")]
    [InlineData("2026_09_01-02_00  Projects")]
    [InlineData("2026_09_01-02_00 Holiday")]           // no longer named like the plan at all
    public void A_folder_copied_or_renamed_by_hand_is_listed_as_renamed_and_not_owned(string folderName)
    {
        VersionFolder.Create(_target, folderName, "p1", bytes: 25, manifestPlanName: "Projects");

        var version = List().Should().ContainSingle().Which;

        version.Name.Should().Be(folderName);
        version.Ownership.Should().Be(VersionOwnership.Renamed);
        version.IsOwned.Should().BeFalse();
        version.TotalBytes.Should().BeNull();
        version.FileCount.Should().BeNull();
    }

    [Fact]
    public void A_folder_renamed_to_the_plans_new_name_by_hand_is_not_owned_either()
    {
        // Made as "Old name"; the plan is called "Projects" now and somebody renamed the folder to match.
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1", manifestPlanName: "Old name");

        List().Should().ContainSingle().Which.Ownership.Should().Be(VersionOwnership.Renamed);
    }

    [Fact]
    public void A_copy_of_another_plans_version_stays_unlisted()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Other - Copy", "other", manifestPlanName: "Other");

        List().Should().BeEmpty();
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
    public void Probe_compares_the_folder_name_with_the_plan_name_in_the_manifest()
    {
        var copy = VersionFolder.Create(_target, "2026_09_01-02_00 Projects - Copy", "p1", manifestPlanName: "Projects");
        var unnamed = VersionFolder.Create(_target, "notes", "p1", manifestPlanName: "Projects");
        var remains = VersionFolder.Create(_target, "2026_09_02-02_00 Projects.deleting", "p1", manifestPlanName: "Projects");
        var partial = VersionFolder.Create(_target, "2026_09_03-02_00 Projects.partial", "p1", manifestPlanName: "Projects");
        var copiedRemains = VersionFolder.Create(_target, "2026_09_04-02_00 Projects - Copy.deleting", "p1", manifestPlanName: "Projects");

        VersionCatalog.Probe(copy, "p1").Should().Be((VersionOwnership.Renamed, (ManifestHeader?)null));
        VersionCatalog.Probe(unnamed, "p1").Should().Be((VersionOwnership.Renamed, (ManifestHeader?)null));
        VersionCatalog.Probe(copiedRemains, "p1").Should().Be((VersionOwnership.Renamed, (ManifestHeader?)null));
        VersionCatalog.Probe(remains, "p1").Ownership.Should().Be(VersionOwnership.Owned, "the suffix is not part of the name");
        VersionCatalog.Probe(partial, "p1").Ownership.Should().Be(VersionOwnership.Owned, "the suffix is not part of the name");
        VersionCatalog.Probe(remains + Path.DirectorySeparatorChar, "p1").Ownership.Should().Be(VersionOwnership.Owned);
        VersionCatalog.Probe(copy, "p2").Ownership.Should().Be(VersionOwnership.Foreign, "the plan id comes first");
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
        Junction.Create(link, real);

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
