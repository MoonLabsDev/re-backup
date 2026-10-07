using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Shared.Json;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Backup;

/// <summary>
/// The version lifecycle of a run (spec 6.1) on in-memory storages: the source is the kind <c>"mem-src"</c>, the target
/// <c>"mem-tgt"</c>; both can be wrapped in a <see cref="FaultyStorage"/>.
/// </summary>
public class BackupRunnerLifecycleTests
{
    private const string Version = "2026_09_30-16_05 Projects";   // 14:05 UTC in the fixed +02:00 test zone
    private const string Pending = Version + "/re-pending.json";
    private static readonly DateTime Mtime = new(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 30, 14, 5, 30, TimeSpan.Zero));
    private readonly InMemoryStorage _source = new();
    private InMemoryStorage _target = new();

    public BackupRunnerLifecycleTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(2), "test", "test"));
        _source.AddFile("a.txt", "alpha"u8.ToArray(), Mtime);
        _source.AddFile("sub/b.bin", "bravo-bravo"u8.ToArray(), Mtime);
        _source.EnsureDirectoryAsync("empty", CancellationToken.None).GetAwaiter().GetResult();
    }

    private static BackupPlan Plan() => new()
    {
        Id = "p1",
        Name = "Projects",
        Source = new StorageLocation("mem-src", "source"),
        Target = new StorageLocation("mem-tgt", "target"),
    };

    private Task<RunLogEntry> Run(Func<IStorage, IStorage>? target = null, Func<IStorage, IStorage>? source = null,
        CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null)
    {
        var storages = new TestStorageFactory(location => location.Kind switch
        {
            "mem-src" => source?.Invoke(_source) ?? _source,
            "mem-tgt" => target?.Invoke(_target) ?? _target,
            _ => throw new NotSupportedException(location.Kind),
        });
        return new BackupRunner(storages, _time, host: "TESTHOST")
            .RunAsync(new BackupRequest(Plan(), [], RunTrigger.Manual), progress, cancellationToken);
    }

    private Task<IReadOnlyList<VersionInfo>> Versions() => VersionCatalog.ListAsync(_target, "p1", "Projects");

    private static bool IsDataFile(string path) => path.EndsWith("/a.txt", StringComparison.Ordinal) ||
                                                   path.EndsWith("/b.bin", StringComparison.Ordinal);

    [Fact]
    public async Task Version_becomes_visible_only_after_pending_marker_is_removed()
    {
        IReadOnlyList<VersionInfo>? beforeMarkerDelete = null;
        var faults = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "delete" && path == Pending)
                {
                    _target.StatAsync(Version + "/re-manifest.json", CancellationToken.None).GetAwaiter().GetResult()
                        .Should().NotBeNull("the manifest is committed before the marker goes");
                    beforeMarkerDelete = Versions().GetAwaiter().GetResult();
                }
            },
        };

        var entry = await Run(faults);

        entry.Status.Should().Be(RunStatus.Completed);
        beforeMarkerDelete.Should().NotBeNull().And.BeEmpty();
        (await Versions()).Should().ContainSingle().Which.Name.Should().Be(Version);
        _target.Files.Should().NotContain(Pending);
    }

    [Fact]
    public async Task The_pending_marker_is_the_first_write_and_names_plan_and_host()
    {
        var faulty = (FaultyStorage?)null;
        MarkerInfo? marker = null;
        var faults = (IStorage s) => faulty = new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "create" && path == Version + "/a.txt")
                    marker = VersionMarkers.TryReadAsync(_target, Pending, CancellationToken.None).GetAwaiter().GetResult();
            },
        };

        await Run(faults);

        faulty!.Created[0].Should().Be(Pending);
        faulty.Created[^1].Should().Be(Version + "/re-manifest.json");
        marker.Should().NotBeNull();
        marker!.PlanId.Should().Be("p1");
        marker.PlanName.Should().Be("Projects");
        marker.Host.Should().Be("TESTHOST");
        marker.StartedUtc.Should().Be(_time.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Cancel_during_copy_leaves_no_files_and_no_marker()
    {
        using var cts = new CancellationTokenSource();
        var faults = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "commit" && path == Version + "/a.txt")
                    cts.Cancel();
            },
        };

        var entry = await Run(faults, cancellationToken: cts.Token);

        entry.Status.Should().Be(RunStatus.Canceled);
        entry.Version.Should().BeNull();
        _target.Files.Should().BeEmpty();
        await foreach (var _ in _target.ListAsync("", recursive: true, CancellationToken.None))
            Assert.Fail("nothing may be left in the target");
    }

    [Fact]
    public async Task A_failure_after_the_manifest_removes_the_manifest_first_the_files_and_the_marker_last()
    {
        var deleted = new List<string>();
        var cleaningUp = false;
        var faults = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation != "delete")
                    return;
                cleaningUp |= path == Version + "/re-manifest.json";
                if (cleaningUp)
                    deleted.Add(path);
            },
            // Removing the marker fails while the run commits; the cleanup's own attempt works.
            FailDelete = path => path == Pending && !cleaningUp,
        };

        var entry = await Run(faults);

        entry.Status.Should().Be(RunStatus.Error);
        entry.Warnings.Should().BeEmpty();
        _target.Files.Should().BeEmpty();
        deleted[0].Should().Be(Version + "/re-manifest.json", "the version stops being one before anything else goes");
        deleted.Should().Contain([Version + "/a.txt", Version + "/sub/b.bin", Version + "/sub", Version + "/empty"]);
        deleted[^2].Should().Be(Pending, "the marker goes last, only the empty folder after it");
        deleted[^1].Should().Be(Version);
    }

    [Fact]
    public async Task Crash_after_manifest_before_marker_delete_is_cleaned_next_run()
    {
        // As if the process died after the manifest: nothing under the version folder can be deleted in this run.
        var crashed = await Run(s => new FaultyStorage(s) { FailDelete = path => path.StartsWith(Version + "/", StringComparison.Ordinal) });

        crashed.Status.Should().Be(RunStatus.Error);
        crashed.Version.Should().BeNull();
        crashed.Warnings.Should().ContainSingle().Which.Should().StartWith($"An unfinished backup could not be deleted (\"{Version}\")");
        _target.Files.Should().Contain([Pending, Version + "/re-manifest.json", Version + "/a.txt"]);
        (await Versions()).Should().BeEmpty("a folder with a pending marker is no version");

        _time.Advance(TimeSpan.FromMinutes(1));
        var next = await Run();

        next.Status.Should().Be(RunStatus.Completed);
        next.Version.Should().Be("2026_09_30-16_06 Projects");
        next.Warnings.Should().BeEmpty();
        _target.Files.Should().NotContain(path => path.StartsWith(Version + "/", StringComparison.Ordinal));
        (await Versions()).Should().ContainSingle().Which.Name.Should().Be("2026_09_30-16_06 Projects");
    }

    [Fact]
    public async Task A_marker_that_cannot_be_deleted_fails_the_run_and_leaves_only_the_marker()
    {
        var entry = await Run(s => new FaultyStorage(s) { FailDelete = path => path == Pending });

        entry.Status.Should().Be(RunStatus.Error);
        entry.Version.Should().BeNull();
        entry.Warnings.Should().ContainSingle().Which.Should().StartWith($"An unfinished backup could not be deleted (\"{Version}\")");
        _target.Files.Should().Equal(Pending);
        (await Versions()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_marker_delete_that_fails_briefly_is_retried()
    {
        var failures = 0;
        var entry = await Run(s => new FaultyStorage(s) { FailDelete = path => path == Pending && failures++ < 2 });

        entry.Status.Should().Be(RunStatus.Completed);
        failures.Should().Be(3);
        (await Versions()).Should().ContainSingle();
    }

    [Fact]
    public async Task Empty_source_directory_is_restorable_from_storage_without_directories()
    {
        _target = new InMemoryStorage(StorageCapabilities.SetModifiedTime);
        var ensured = new List<string>();

        var entry = await Run(s => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "ensure")
                    ensured.Add(path);
            },
        });

        entry.Status.Should().Be(RunStatus.Completed);
        ensured.Should().NotContain(path => path.StartsWith(Version, StringComparison.Ordinal),
            "folders are not created on a storage without them");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            _target.ReadAllBytes(Version + "/re-manifest.json"), JsonDefaults.Options)!;
        manifest.FormatVersion.Should().Be(2);
        manifest.Directories.Should().BeEquivalentTo("empty", "sub");
        _target.Files.Should().BeEquivalentTo(Version + "/a.txt", Version + "/sub/b.bin", Version + "/re-manifest.json");
    }

    [Fact]
    public async Task Empty_directories_are_created_on_a_storage_that_has_them()
    {
        var entry = await Run();

        entry.Status.Should().Be(RunStatus.Completed);
        (await _target.StatAsync(Version + "/empty", CancellationToken.None)).Should().NotBeNull()
            .And.Subject.As<StorageEntry>().IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task Files_carry_the_source_time_content_and_hash()
    {
        var entry = await Run();

        entry.Status.Should().Be(RunStatus.Completed);
        entry.FilesCopied.Should().Be(2);
        entry.BytesCopied.Should().Be(16);
        Encoding.UTF8.GetString(_target.ReadAllBytes(Version + "/sub/b.bin")).Should().Be("bravo-bravo");
        (await _target.StatAsync(Version + "/a.txt", CancellationToken.None))!.ModifiedUtc.Should().Be(Mtime);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            _target.ReadAllBytes(Version + "/re-manifest.json"), JsonDefaults.Options)!;
        manifest.Files.Single(f => f.Path == "a.txt").MtimeUtc.Should().Be(Mtime);
        manifest.Source.Should().Be("source");
    }

    [Fact]
    public async Task Disk_full_reports_full_status()
    {
        var entry = await Run(s => new FaultyStorage(s) { DiskFullOnWrite = IsDataFile });

        entry.Status.Should().Be(RunStatus.Full);
        entry.Reason.Should().Be("The target ran out of space during the backup.");
        _target.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Disk_full_while_writing_the_manifest_reports_full_status()
    {
        var entry = await Run(s => new FaultyStorage(s) { DiskFullOnWrite = path => path.EndsWith("re-manifest.json", StringComparison.Ordinal) });

        entry.Status.Should().Be(RunStatus.Full);
        _target.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Disk_full_while_reserving_the_name_reports_full_status_and_leaves_nothing()
    {
        var entry = await Run(s => new FaultyStorage(s) { DiskFullOnWrite = path => path == Pending });

        entry.Status.Should().Be(RunStatus.Full);
        _target.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Free_space_preflight_is_skipped_when_unknown()
    {
        _target.FreeSpace = null;

        var entry = await Run();

        entry.Status.Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task Free_space_preflight_applies_when_known()
    {
        _target.FreeSpace = 16;   // 16 bytes + 5 % does not fit

        var entry = await Run();

        entry.Status.Should().Be(RunStatus.Full);
        entry.Reason.Should().Contain("is free on the target");
        _target.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Name_reservation_waits_for_next_minute_on_conflict()
    {
        // Another run takes the name between the check and the exclusive create.
        var faults = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "create" && path == Pending && !_target.Files.Contains(Pending))
                    _target.AddFile(Pending, "{\"formatVersion\":1,\"planId\":\"p1\",\"planName\":\"Projects\"}"u8.ToArray());
            },
        };

        var entry = await RunAdvancingTheClock(() => Run(faults));

        entry.Status.Should().Be(RunStatus.Completed);
        entry.Version.Should().NotBe(Version);
        VersionName.TryParse(entry.Version!, "Projects", out var versionTime).Should().BeTrue();
        versionTime.Should().BeAfter(new DateTime(2026, 9, 30, 16, 5, 0));
        _target.Files.Where(path => path.StartsWith(Version + "/", StringComparison.Ordinal)).Should().Equal(Pending);
    }

    [Theory]
    [InlineData(Version + "/re-deleting.json", "deleting marker of another plan")]
    [InlineData(Version + "/re-pending.json", "pending marker of another plan")]
    [InlineData(Version + ".partial/re-manifest.json", "legacy partial folder with a manifest")]
    [InlineData(Version + ".deleting/re-manifest.json", "legacy deleting folder of another plan")]
    public async Task Name_reservation_skips_a_name_whose_folder_exists_in_any_form(string existing, string because)
    {
        _target.AddFile(existing, "{\"formatVersion\":1,\"planId\":\"other\",\"planName\":\"Projects\"}"u8.ToArray());

        var entry = await RunAdvancingTheClock(() => Run());

        entry.Status.Should().Be(RunStatus.Completed, because);
        entry.Version.Should().NotBe(Version, because);
        _target.Files.Where(path => path.StartsWith(Version, StringComparison.Ordinal)).Should().Equal([existing], because);
    }

    [Theory]
    [InlineData("locked", "locked by another program")]
    [InlineData("missing", "no longer exists")]
    [InlineData("denied", "access denied")]
    [InlineData("io", "cannot be opened: the device hiccuped")]
    [InlineData("name", "cannot be opened: A path segment must not end with a space or a dot.")]
    public async Task A_source_file_that_cannot_be_opened_is_skipped(string failure, string reason)
    {
        var source = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation != "open" || path != "a.txt")
                    return;
                throw failure switch
                {
                    "locked" => new StorageLockedException(path),
                    "missing" => new StorageNotFoundException(path),
                    "denied" => new StorageAccessDeniedException(path),
                    "io" => new StorageIOException(path, inner: new IOException("the device hiccuped")),
                    _ => new ArgumentException("A path segment must not end with a space or a dot."),
                };
            },
        };

        var entry = await Run(source: source);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.Skipped.Should().ContainSingle().Which.Should().Be(new SkippedEntry("a.txt", reason));
        entry.FilesCopied.Should().Be(1);
        _target.Files.Should().NotContain(Version + "/a.txt").And.Contain(Version + "/sub/b.bin");
    }

    [Fact]
    public async Task A_source_whose_root_is_gone_after_a_failed_open_aborts()
    {
        var opened = false;
        var source = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "open")
                    opened = true;
                if (opened && operation is "open" or "stat")
                    throw new StorageUnavailableException(path);
            },
        };

        var entry = await Run(source: source);

        entry.Status.Should().Be(RunStatus.Error);
        entry.Reason.Should().Be("The source folder is no longer available.");
        _target.Files.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_changed_during_the_copy_is_reported_as_changed()
    {
        var source = (IStorage s) => new FaultyStorage(s)
        {
            Before = (operation, path) =>
            {
                if (operation == "stat" && path == "a.txt")
                    _source.AddFile("a.txt", "alpha-more"u8.ToArray(), Mtime.AddMinutes(1));
            },
        };

        var entry = await Run(source: source);

        entry.Status.Should().Be(RunStatus.CompletedWithWarnings);
        entry.Skipped.Should().ContainSingle().Which.Should().Be(
            new SkippedEntry("a.txt", "changed while it was copied; the copy may be inconsistent"));
        entry.FilesCopied.Should().Be(2);
    }

    private async Task<RunLogEntry> RunAdvancingTheClock(Func<Task<RunLogEntry>> start)
    {
        var run = start();
        var waited = Stopwatch.StartNew();
        while (!run.IsCompleted && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(25);
            _time.Advance(TimeSpan.FromSeconds(10));
        }
        run.IsCompleted.Should().BeTrue();
        return await run;
    }
}
