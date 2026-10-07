using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage;
using ReBackup.Storage.FileSystem;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

public class VersionCatalogTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _target;

    public VersionCatalogTests() => _target = _tmp.CreateDir("target");

    public void Dispose() => _tmp.Dispose();

    private IStorage Target => new FileSystemStorage(_target);

    private Task<IReadOnlyList<VersionInfo>> List() => VersionCatalog.ListAsync(Target, "p1", "Projects");

    private Task<(VersionOwnership Ownership, ManifestHeader? Header)> Probe(string versionPath, string planId) =>
        VersionCatalog.ProbeAsync(Target, versionPath, planId);

    [Fact]
    public async Task Lists_the_versions_of_the_plan_oldest_first_with_their_totals()
    {
        var newer = VersionFolder.Create(_target, "2026_09_30-14_05 Projects", "p1", bytes: 30);
        VersionFolder.Create(_target, "2026_09_28-02_00 Projects", "p1", bytes: 10);

        var versions = await List();

        versions.Select(v => v.Name).Should().Equal("2026_09_28-02_00 Projects", "2026_09_30-14_05 Projects");
        versions.Should().OnlyContain(v => v.IsOwned && v.Ownership == VersionOwnership.Owned);
        versions[0].LocalTime.Should().Be(new DateTime(2026, 9, 28, 2, 0, 0));
        versions[1].Path.Should().Be("2026_09_30-14_05 Projects", "the path is relative to the target");
        Path.Combine(_target, versions[1].Path).Should().Be(newer);
        versions.Select(v => v.TotalBytes).Should().Equal(10L, 30L);
        versions.Select(v => v.FileCount).Should().Equal(1, 1);
    }

    [Theory]
    [InlineData("2026_09_29-02_00 Projects.")]
    [InlineData("2026_09_29-02_00 Projects ")]
    public async Task A_folder_whose_name_the_storage_cannot_address_is_left_out(string name)
    {
        VersionFolder.Create(_target, "2026_09_28-02_00 Projects", "p1");
        // Windows creates such a name (as WSL or a share might) only through the \\?\ prefix.
        var odd = @"\\?\" + Path.Combine(_target, name);
        Directory.CreateDirectory(odd);
        File.WriteAllText(Path.Combine(odd, "re-manifest.json"), "{}");
        try
        {
            var versions = await List();

            versions.Select(v => v.Name).Should().Equal("2026_09_28-02_00 Projects");
        }
        finally
        {
            Directory.Delete(odd, recursive: true);
        }
    }

    [Fact]
    public async Task Versions_made_under_an_earlier_plan_name_stay_with_the_plan()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Old name", "p1");

        (await List()).Should().ContainSingle().Which.Ownership.Should().Be(VersionOwnership.Owned);
    }

    [Fact]
    public async Task A_version_is_owned_whatever_the_case_of_its_name()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 PROJECTS", "p1", manifestPlanName: "projects");

        (await List()).Should().ContainSingle().Which.Ownership.Should().Be(VersionOwnership.Owned);
    }

    [Theory]
    [InlineData("2026_09_01-02_00 Projects - Copy")]   // an Explorer copy
    [InlineData("2026_09_01-02_00 Projects KEEP")]     // renamed to protect it
    [InlineData("2026_09_01-02_00 Project")]
    [InlineData("2026_09_01-02_00  Projects")]
    [InlineData("2026_09_01-02_00 Holiday")]           // no longer named like the plan at all
    public async Task A_folder_copied_or_renamed_by_hand_is_listed_as_renamed_and_not_owned(string folderName)
    {
        VersionFolder.Create(_target, folderName, "p1", bytes: 25, manifestPlanName: "Projects");

        var version = (await List()).Should().ContainSingle().Which;

        version.Name.Should().Be(folderName);
        version.Ownership.Should().Be(VersionOwnership.Renamed);
        version.IsOwned.Should().BeFalse();
        version.TotalBytes.Should().BeNull();
        version.FileCount.Should().BeNull();
    }

    [Fact]
    public async Task A_folder_renamed_to_the_plans_new_name_by_hand_is_not_owned_either()
    {
        // Made as "Old name"; the plan is called "Projects" now and somebody renamed the folder to match.
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1", manifestPlanName: "Old name");

        (await List()).Should().ContainSingle().Which.Ownership.Should().Be(VersionOwnership.Renamed);
    }

    [Fact]
    public async Task A_copy_of_another_plans_version_stays_unlisted()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Other - Copy", "other", manifestPlanName: "Other");

        (await List()).Should().BeEmpty();
    }

    [Fact]
    public async Task Folders_named_like_the_plan_but_not_owned_are_listed_as_such()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "other");
        _tmp.WriteFile(@"target\2026_09_02-02_00 projects\a.txt", "x");
        _tmp.WriteFile(@"target\2026_09_03-02_00 Projects\re-manifest.json", "not json");

        var versions = await List();

        versions.Select(v => v.Ownership).Should().Equal(
            VersionOwnership.Foreign, VersionOwnership.NoManifest, VersionOwnership.Unreadable);
        versions.Should().OnlyContain(v => !v.IsOwned && v.TotalBytes == null && v.FileCount == null);
    }

    [Fact]
    public async Task Other_folders_are_not_listed()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Other", "other");
        VersionFolder.Create(_target, "2026_09_02-02_00 Projects.partial", "p1");
        VersionFolder.Create(_target, "2026_09_03-02_00 Projects.deleting", "p1");
        VersionFolder.Create(_target, "2026_13_04-02_00 Projects", "p1");
        VersionFolder.Create(_target, "notes", "p1");
        _tmp.WriteFile(@"target\2026_09_05-02_00 Projects", "a file, not a folder");

        (await List()).Should().BeEmpty();
    }

    [Fact]
    public async Task Totals_of_a_manifest_without_them_are_summed_up_from_its_file_list()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1", bytes: 25, withTotals: false);

        var version = (await List()).Single();

        version.TotalBytes.Should().Be(25);
        version.FileCount.Should().Be(1);
    }

    [Fact]
    public async Task A_missing_target_has_no_versions()
    {
        (await VersionCatalog.ListAsync(new FileSystemStorage(_tmp.PathOf("nowhere")), "p1", "Projects")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_plan_id_owns_nothing()
    {
        _tmp.WriteFile(@"target\2026_09_01-02_00 Projects\re-manifest.json", "{}");

        (await VersionCatalog.ListAsync(Target, "", "Projects")).Should().ContainSingle()
            .Which.Ownership.Should().Be(VersionOwnership.Foreign);
    }

    [Fact]
    public async Task Probe_tells_whose_version_a_folder_is()
    {
        const string own = "2026_09_01-02_00 Projects";
        VersionFolder.Create(_target, own, "p1", bytes: 7);
        _tmp.CreateDir(@"target\empty");

        var (ownership, header) = await Probe(own, "P1");
        ownership.Should().Be(VersionOwnership.Owned, "plan ids are compared without regard to case");
        header!.TotalBytes.Should().Be(7);

        (await Probe(own, "p2")).Should().Be((VersionOwnership.Foreign, (ManifestHeader?)null));
        (await Probe("empty", "p1")).Should().Be((VersionOwnership.NoManifest, (ManifestHeader?)null));
        (await Probe("nowhere", "p1")).Should().Be((VersionOwnership.NoManifest, (ManifestHeader?)null));
    }

    [Fact]
    public async Task Probe_compares_the_folder_name_with_the_plan_name_in_the_manifest()
    {
        var copy = Path.GetFileName(VersionFolder.Create(_target, "2026_09_01-02_00 Projects - Copy", "p1", manifestPlanName: "Projects"));
        var unnamed = Path.GetFileName(VersionFolder.Create(_target, "notes", "p1", manifestPlanName: "Projects"));
        var remains = Path.GetFileName(VersionFolder.Create(_target, "2026_09_02-02_00 Projects.deleting", "p1", manifestPlanName: "Projects"));
        var partial = Path.GetFileName(VersionFolder.Create(_target, "2026_09_03-02_00 Projects.partial", "p1", manifestPlanName: "Projects"));
        var copiedRemains = Path.GetFileName(VersionFolder.Create(_target, "2026_09_04-02_00 Projects - Copy.deleting", "p1", manifestPlanName: "Projects"));

        (await Probe(copy, "p1")).Should().Be((VersionOwnership.Renamed, (ManifestHeader?)null));
        (await Probe(unnamed, "p1")).Should().Be((VersionOwnership.Renamed, (ManifestHeader?)null));
        (await Probe(copiedRemains, "p1")).Should().Be((VersionOwnership.Renamed, (ManifestHeader?)null));
        (await Probe(remains, "p1")).Ownership.Should().Be(VersionOwnership.Owned, "the suffix is not part of the name");
        (await Probe(partial, "p1")).Ownership.Should().Be(VersionOwnership.Owned, "the suffix is not part of the name");
        (await Probe(copy, "p2")).Ownership.Should().Be(VersionOwnership.Foreign, "the plan id comes first");
    }

    [Fact]
    public async Task A_canceled_token_stops_the_listing()
    {
        VersionFolder.Create(_target, "2026_09_01-02_00 Projects", "p1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => VersionCatalog.ListAsync(Target, "p1", "Projects", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_folder_that_is_a_link_is_not_listed()
    {
        var real = VersionFolder.Create(_tmp.CreateDir("elsewhere"), "2026_09_01-02_00 Projects", "p1");
        var link = Path.Combine(_target, "2026_09_01-02_00 Projects");
        Junction.Create(link, real);

        try
        {
            (await List()).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(link);   // removes the junction only
        }
    }

    [Fact]
    public async Task Folder_with_pending_marker_is_not_a_version()
    {
        var storage = new InMemoryStorage();
        await VersionFolder.CreateAsync(storage, "2026_09_01-02_00 Projects", "p1");
        var writing = await VersionFolder.CreateAsync(storage, "2026_09_02-02_00 Projects", "p1");
        await VersionFolder.WriteAsync(storage, StoragePath.Combine(writing, VersionMarkerNames.Pending), "{}"u8.ToArray());

        var versions = await VersionCatalog.ListAsync(storage, "p1", "Projects");

        versions.Select(v => v.Name).Should().Equal("2026_09_01-02_00 Projects");
        versions.Single().Path.Should().Be("2026_09_01-02_00 Projects");
    }

    [Fact]
    public async Task Folder_with_deleting_marker_is_not_a_version()
    {
        var storage = new InMemoryStorage();
        await VersionFolder.CreateAsync(storage, "2026_09_01-02_00 Projects", "p1");
        var doomed = await VersionFolder.CreateAsync(storage, "2026_09_02-02_00 Projects", "p1");
        await VersionFolder.WriteAsync(storage, StoragePath.Combine(doomed, VersionMarkerNames.Deleting), "{}"u8.ToArray());

        var versions = await VersionCatalog.ListAsync(storage, "p1", "Projects");

        versions.Select(v => v.Name).Should().Equal("2026_09_01-02_00 Projects");
    }

    [Fact]
    public async Task Catalog_of_missing_root_is_empty()
    {
        var storage = new FileSystemStorage(_tmp.PathOf("not-created-yet"));

        (await VersionCatalog.ListAsync(storage, "p1", "Projects")).Should().BeEmpty();
    }

    [Fact]
    public async Task Catalog_of_unavailable_root_throws_unavailable()
    {
        var storage = new WrappedStorage(new InMemoryStorage()) { Unavailable = true };

        var act = () => VersionCatalog.ListAsync(storage, "p1", "Projects");

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task Versions_from_1_0_5_are_listed_as_owned()
    {
        VersionBuilder.Write(_target, "2026_09_01-02_00", [VersionBuilder.File("a.txt", "alpha")], formatVersion: 1);
        File.ReadAllText(Path.Combine(_target, "2026_09_01-02_00 Projects", VersionName.ManifestFileName))
            .Should().NotContain("directories", "1.0.5 wrote no directory list");

        var version = (await VersionCatalog.ListAsync(Target, VersionBuilder.PlanId, VersionBuilder.PlanName)).Should().ContainSingle().Which;

        version.Ownership.Should().Be(VersionOwnership.Owned);
        version.FileCount.Should().Be(1);
        version.TotalBytes.Should().Be(5);
    }

    [Fact]
    public async Task Probe_of_an_unreadable_manifest_is_unreadable()
    {
        var storage = new InMemoryStorage();
        await VersionFolder.WriteAsync(storage, "2026_09_01-02_00 Projects/re-manifest.json", "not json"u8.ToArray());
        var offline = new WrappedStorage(storage) { Unavailable = true };

        (await VersionCatalog.ProbeAsync(storage, "2026_09_01-02_00 Projects", "p1")).Ownership.Should().Be(VersionOwnership.Unreadable);
        (await VersionCatalog.ProbeAsync(offline, "2026_09_01-02_00 Projects", "p1")).Ownership.Should().Be(VersionOwnership.Unreadable);
        (await VersionCatalog.ProbeAsync(storage, "2026_09_02-02_00 Projects", "p1")).Ownership.Should().Be(VersionOwnership.NoManifest);
    }

    [Fact]
    public async Task Root_lists_but_child_unavailable_makes_ListAsync_throw_unavailable()
    {
        var inner = new InMemoryStorage();
        await VersionFolder.CreateAsync(inner, "2026_09_01-02_00 Projects", "p1");
        var storage = new FaultyStorage(inner)
        {
            Before = (operation, path) =>
            {
                if (operation == "open")   // the probe reads the manifest: the share drops out right then
                    throw new StorageUnavailableException(path);
            },
        };

        var act = () => VersionCatalog.ListAsync(storage, "p1", "Projects");

        await act.Should().ThrowAsync<StorageUnavailableException>();
    }

    [Fact]
    public async Task The_in_transit_check_looks_at_the_two_markers_of_listed_candidates_only()
    {
        var inner = new InMemoryStorage();
        await VersionFolder.CreateAsync(inner, "2026_09_01-02_00 Projects", "p1");
        await VersionFolder.CreateAsync(inner, "2026_09_02-02_00 Other", "p2");   // another plan's: not a candidate
        inner.AddFile("notes/a.txt", [1]);
        var calls = new List<(string Operation, string Path)>();
        var storage = new FaultyStorage(inner) { Before = (operation, path) => calls.Add((operation, path)) };

        (await VersionCatalog.ListAsync(storage, "p1", "Projects")).Should().ContainSingle();

        calls.Where(c => c.Operation == "list").Should().Equal(("list", ""));
        calls.Where(c => c.Operation == "stat").Should().BeEquivalentTo(new[]
        {
            ("stat", "2026_09_01-02_00 Projects/re-pending.json"),
            ("stat", "2026_09_01-02_00 Projects/re-deleting.json"),
        });
    }
}
