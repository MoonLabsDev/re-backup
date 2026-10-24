using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using ReBackup.App.Control;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Control;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Shared.IO;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.App.Tests;

/// <summary>
/// The control service's read operations: what the MCP bridge gets back for plans, versions, ignore rules, files and
/// run history. Only metadata: never the content of a file.
/// </summary>
public class ControlReadTests : IDisposable
{
    private const string Secret = "TOPSECRET";
    private const string Older = "2026_09_29-14_05 Projects";
    private const string Newer = "2026_09_30-14_05 Projects";
    private const string Unindexed = "2026_10_01-14_05 Projects";
    private const string NestedIgnore = "# scratch files\n*.tmp\n";

    private readonly MainViewModelFixture _fixture = new();
    private readonly SerialContext _ui = new();
    private readonly string _source;
    private readonly string _target;

    public ControlReadTests()
    {
        _source = Path.Combine(_fixture.Root, "source");
        _target = Path.Combine(_fixture.Root, "target");
        WriteFile(_source, "a.txt", Secret + "-alpha");          // 15 bytes
        WriteFile(_source, "old.bak", Secret + "-bak");          // 13
        WriteFile(_source, "logs/x.log", Secret + "-log-1");     // 15
        WriteFile(_source, "logs/y.log", Secret + "-log-22");    // 16
        WriteFile(_source, "docs/readme.md", Secret + "-readme"); // 16
        WriteFile(_source, "docs/scratch.tmp", Secret + "-t");   // 11
        WriteFile(_source, "docs/.backupignore", NestedIgnore);  // 22
        Directory.CreateDirectory(_target);

        _fixture.Settings.DefaultIgnorePatterns = ["*.bak", "Thumbs.db"];
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p1",
            Name = "Projects",
            Source = StorageLocation.FileSystem(_source),
            Target = StorageLocation.FileSystem(_target),
            Ignore = new IgnoreSettings { Patterns = ["# build output", "logs/"] },
        });
    }

    public void Dispose()
    {
        _ui.Dispose();
        _fixture.Dispose();
    }

    // ------------------------------------------------------------------ list_plans

    [Fact]
    public async Task List_plans_shows_saved_plans_with_locations_kind_state_and_last_run()
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "p2", Name = "Photos", Incremental = true,
            Source = StorageLocation.FileSystem(@"C:\Photos"), Target = StorageLocation.FileSystem(@"D:\Backup"),
        });
        AppendRun("p1", new RunLogEntry
        {
            RunId = "r1", Trigger = RunTrigger.Manual, Status = RunStatus.Completed, Version = Older,
            StartUtc = Utc(2026, 9, 29, 12, 5), EndUtc = Utc(2026, 9, 29, 12, 6),
        });
        var (service, vm) = Create();
        vm.NewPlanCommand.Execute(null);   // unsaved: not listed

        var result = await OkAsync(service, ControlOps.ListPlans, new { });

        var plans = result.GetProperty("plans").EnumerateArray().ToList();
        plans.Select(p => p.GetProperty("id").GetString()).Should().Equal(["p2", "p1"], "the plans come in the app's order (by name)");
        var p1 = plans[1];
        p1.GetProperty("name").GetString().Should().Be("Projects");
        p1.GetProperty("source").GetString().Should().Be(_source);
        p1.GetProperty("target").GetString().Should().Be(_target);
        p1.GetProperty("kind").GetString().Should().Be("full");
        p1.GetProperty("state").GetString().Should().Be("idle");
        p1.GetProperty("lastRun").GetProperty("status").GetString().Should().Be("completed");
        p1.GetProperty("lastRun").GetProperty("trigger").GetString().Should().Be("manual");
        p1.GetProperty("lastRun").GetProperty("start").GetDateTime().Should().Be(Utc(2026, 9, 29, 12, 5));
        p1.TryGetProperty("nextRun", out _).Should().BeFalse("the plan has no triggers");
        result.GetProperty("total").GetInt32().Should().Be(2);
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
        plans[0].GetProperty("kind").GetString().Should().Be("incremental");
        plans[0].TryGetProperty("lastRun", out _).Should().BeFalse("it never ran");
    }

    // ------------------------------------------------------------------ plan argument

    [Fact]
    public async Task Plan_by_name_is_case_insensitive()
    {
        var (service, _) = Create();

        var byName = await OkAsync(service, ControlOps.GetRunHistory, new { plan = "pROJECTS" });
        var byId = await OkAsync(service, ControlOps.GetRunHistory, new { plan = "P1" });

        byName.GetProperty("plan").GetString().Should().Be("Projects");
        byId.GetProperty("plan").GetString().Should().Be("Projects");
    }

    [Fact]
    public async Task Ambiguous_plan_name_lists_candidates()
    {
        _fixture.Store.Save(new BackupPlan { Id = "d1", Name = "Dup" });
        _fixture.Store.Save(new BackupPlan { Id = "d2", Name = "DUP" });
        var (service, _) = Create();

        var error = await FailAsync(service, ControlOps.GetRunHistory, new { plan = "dup" });

        error.Key.Should().Be(ControlErrors.PlanNotFound);
        error.Message.Should().Contain("d1").And.Contain("d2").And.Contain("Dup").And.Contain("DUP");
    }

    [Fact]
    public async Task Unknown_plan_is_plan_not_found()
    {
        var (service, _) = Create();

        var error = await FailAsync(service, ControlOps.ListVersions, new { plan = "Nope" });

        error.Key.Should().Be(ControlErrors.PlanNotFound);
        error.Message.Should().Contain("Nope").And.Contain("Projects", "the message names the plans there are");
    }

    // ------------------------------------------------------------------ list_versions

    [Fact]
    public async Task List_versions_shows_indexed_versions_newest_first_and_flags_unindexed_ones()
    {
        AppendRun("p1", new RunLogEntry
        {
            RunId = "r2", Trigger = RunTrigger.Scheduled, Status = RunStatus.CompletedWithWarnings, Version = Newer,
            SkippedCount = 2, Warnings = ["Retention could not delete X."],
            StartUtc = Utc(2026, 9, 30, 12, 5), EndUtc = Utc(2026, 9, 30, 12, 6),
        });
        var (service, _) = await CreateWithVersionsAsync();

        var result = await OkAsync(service, ControlOps.ListVersions, new { plan = "Projects" });

        var versions = result.GetProperty("versions").EnumerateArray().ToList();
        versions.Select(v => v.GetProperty("name").GetString()).Should().Equal(Unindexed, Newer, Older);

        var newer = versions[1];
        newer.GetProperty("indexed").GetBoolean().Should().BeTrue();
        newer.GetProperty("files").GetInt32().Should().Be(3);
        newer.GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(26);
        newer.GetProperty("size").GetProperty("text").GetString().Should().Be(ByteSize.Format(26));
        newer.GetProperty("date").GetDateTimeOffset().DateTime.Should().Be(new DateTime(2026, 9, 30, 14, 5, 0));
        newer.TryGetProperty("exclusive", out _).Should().BeFalse("a full plan shares nothing between versions");
        newer.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Equal(
            "2 entries were skipped in its run.", "Retention could not delete X.");

        versions[2].GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(7);
        versions[2].GetProperty("warnings").GetArrayLength().Should().Be(0);

        var unindexed = versions[0];
        unindexed.GetProperty("indexed").GetBoolean().Should().BeFalse();
        unindexed.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should()
            .ContainSingle(w => w!.Contains("not indexed"));
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    // ------------------------------------------------------------------ get_ignore_rules

    [Fact]
    public async Task Get_ignore_rules_lists_every_origin_with_hits_per_line()
    {
        var (service, _) = Create();

        var result = await OkAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects" });

        result.GetProperty("useGlobalDefaults").GetBoolean().Should().BeTrue();
        result.GetProperty("honorNestedFiles").GetBoolean().Should().BeTrue();
        Hits(result.GetProperty("planPatterns")).Should().Equal((2, "logs/", 1, 31L));
        var globals = result.GetProperty("globalDefaults");
        globals.GetProperty("used").GetBoolean().Should().BeTrue();
        Hits(globals.GetProperty("lines")).Should().Equal((1, "*.bak", 1, 13L), (2, "Thumbs.db", 0, 0L));

        var file = result.GetProperty("ignoreFiles").EnumerateArray().Single();
        file.GetProperty("path").GetString().Should().Be("docs/.backupignore");
        file.GetProperty("content").GetString().Should().Be("# scratch files\n*.tmp");
        Hits(file.GetProperty("lines")).Should().Equal((2, "*.tmp", 1, 11L));
        result.GetProperty("scannedFiles").GetInt32().Should().Be(7);
    }

    // ------------------------------------------------------------------ explain_path

    [Fact]
    public async Task Explain_path_names_the_deciding_pattern_its_origin_and_line()
    {
        var (service, _) = Create();

        var log = await OkAsync(service, ControlOps.ExplainPath, new { plan = "Projects", path = @"logs\x.log" });
        log.GetProperty("path").GetString().Should().Be("logs/x.log");
        log.GetProperty("exists").GetBoolean().Should().BeTrue();
        log.GetProperty("kind").GetString().Should().Be("file");
        log.GetProperty("ignored").GetBoolean().Should().BeTrue();
        log.GetProperty("ignoredFolder").GetString().Should().Be("logs");
        Rule(log).Should().Be(("logs/", "Plan", 2, false));

        var tmp = await OkAsync(service, ControlOps.ExplainPath, new { plan = "Projects", path = "docs/scratch.tmp" });
        Rule(tmp).Should().Be(("*.tmp", "docs/.backupignore", 2, false));
        tmp.TryGetProperty("ignoredFolder", out _).Should().BeFalse();

        var readme = await OkAsync(service, ControlOps.ExplainPath, new { plan = "Projects", path = "docs/readme.md" });
        readme.GetProperty("ignored").GetBoolean().Should().BeFalse();
        readme.TryGetProperty("rule", out _).Should().BeFalse();

        var missing = await OkAsync(service, ControlOps.ExplainPath, new { plan = "Projects", path = "nothing/here.bak" });
        missing.GetProperty("exists").GetBoolean().Should().BeFalse();
        Rule(missing).Should().Be(("*.bak", "Global defaults", 1, false));
    }

    // ------------------------------------------------------------------ list_files

    [Fact]
    public async Task List_files_of_the_source_shows_sizes_counts_and_ignore_decisions()
    {
        var (service, _) = Create();

        var result = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects", path = "", @in = "source" });

        var entries = result.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("name").GetString()).Should().Equal("docs", "logs", "a.txt", "old.bak");
        var docs = entries[0];
        docs.GetProperty("kind").GetString().Should().Be("folder");
        docs.GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(49);
        docs.GetProperty("files").GetInt32().Should().Be(3);
        docs.GetProperty("ignored").GetBoolean().Should().BeFalse();
        docs.GetProperty("partlyIgnored").GetBoolean().Should().BeTrue();
        var logs = entries[1];
        logs.GetProperty("ignored").GetBoolean().Should().BeTrue();
        Rule(logs).Should().Be(("logs/", "Plan", 2, false));
        var a = entries[2];
        a.GetProperty("kind").GetString().Should().Be("file");
        a.GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(15);
        a.GetProperty("extension").GetString().Should().Be(".txt");
        a.GetProperty("date").GetDateTime().Should().Be(File.GetLastWriteTimeUtc(Path.Combine(_source, "a.txt")));
        a.TryGetProperty("files", out _).Should().BeFalse();
        Rule(entries[3]).Should().Be(("*.bak", "Global defaults", 1, false));
        result.GetProperty("total").GetInt32().Should().Be(4);
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();

        var bySize = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects", sort = "size" });
        bySize.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().Equal("docs", "logs", "a.txt", "old.bak");

        var inDocs = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects", path = "docs" });
        inDocs.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().Equal(".backupignore", "readme.md", "scratch.tmp");
    }

    [Fact]
    public async Task List_files_of_a_version_reads_the_index()
    {
        var (service, _) = await CreateWithVersionsAsync();

        var root = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects", @in = Newer });
        var entries = root.GetProperty("entries").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("name").GetString()).Should().Equal("sub", "a.txt", "new.txt");
        entries[0].GetProperty("kind").GetString().Should().Be("folder");
        entries[0].GetProperty("files").GetInt32().Should().Be(1);
        entries[0].GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(11);
        entries[1].GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(12);
        entries[1].TryGetProperty("ignored", out _).Should().BeFalse("a version holds what was backed up");

        var sub = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects", path = "sub", @in = Newer });
        sub.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).Should().Equal("deep.txt");
    }

    [Fact]
    public async Task List_files_of_a_version_that_is_not_indexed_or_unknown_fails()
    {
        var (service, _) = await CreateWithVersionsAsync();

        (await FailAsync(service, ControlOps.ListFiles, new { plan = "Projects", @in = Unindexed })).Key
            .Should().Be(ControlErrors.VersionNotIndexed);
        (await FailAsync(service, ControlOps.ListFiles, new { plan = "Projects", @in = "2020_01_01-00_00 Projects" })).Key
            .Should().Be(ControlErrors.VersionNotFound);
    }

    [Fact]
    public async Task List_files_caps_at_200_with_a_hint()
    {
        var big = Path.Combine(_fixture.Root, "big");
        for (var i = 0; i < 250; i++)
            WriteFile(big, $"f{i:000}.dat", "x");
        _fixture.Store.Save(new BackupPlan { Id = "p3", Name = "Big", Source = StorageLocation.FileSystem(big) });
        var (service, _) = Create();

        var result = await OkAsync(service, ControlOps.ListFiles, new { plan = "Big", limit = 1000 });

        result.GetProperty("entries").GetArrayLength().Should().Be(200);
        result.GetProperty("total").GetInt32().Should().Be(250);
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("hint").GetString().Should().Contain("path").And.Contain("limit");

        var limited = await OkAsync(service, ControlOps.ListFiles, new { plan = "Big", limit = 10 });
        limited.GetProperty("entries").GetArrayLength().Should().Be(10);
        limited.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task List_files_in_a_folder_the_source_does_not_have_is_a_bad_request()
    {
        var (service, _) = Create();

        var error = await FailAsync(service, ControlOps.ListFiles, new { plan = "Projects", path = "nope" });

        error.Key.Should().Be(ControlErrors.BadRequest);
        error.Message.Should().Contain("nope");
    }

    // ------------------------------------------------------------------ the source scan

    [Fact]
    public async Task Missing_source_is_a_bad_request_naming_the_path()
    {
        var missing = Path.Combine(_fixture.Root, "gone");
        _fixture.Store.Save(new BackupPlan { Id = "p4", Name = "Gone", Source = StorageLocation.FileSystem(missing) });
        var (service, _) = Create();

        var error = await FailAsync(service, ControlOps.GetIgnoreRules, new { plan = "Gone" });

        error.Key.Should().Be(ControlErrors.BadRequest);
        error.Message.Should().Be("The plan's source does not exist: " + missing);
    }

    [Fact]
    public async Task Scan_that_is_not_finished_in_time_is_scan_running_with_progress()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Storages = new HoldingFactory(_source, gate.Task);
        var (service, _) = Create();
        service.ScanWait = TimeSpan.FromMilliseconds(100);

        var error = await FailAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects" });

        error.Key.Should().Be(ControlErrors.ScanRunning);
        error.Message.Should().Contain("0 files").And.Contain("0 folders");
        gate.SetResult();
        service.ScanWait = TimeSpan.FromSeconds(20);
        (await OkAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects" }))
            .GetProperty("scannedFiles").GetInt32().Should().Be(7, "the retry joins the scan that went on");
    }

    // ------------------------------------------------------------------ scan freshness

    [Fact]
    public async Task Every_scan_based_answer_says_when_the_scan_was_made()
    {
        var (service, _) = await CreateWithVersionsAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var answers = new[]
        {
            await OkAsync(service, ControlOps.CompareSourceWithVersion, new { plan = "Projects", version = Newer }),
            await OkAsync(service, ControlOps.PreviewIgnoreChange, new { plan = "Projects", add = new[] { "*.md" } }),
            await OkAsync(service, ControlOps.ExplainPath, new { plan = "Projects", path = "a.txt" }),
            await OkAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects" }),
            await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects" }),
        };

        foreach (var answer in answers)
        {
            var text = answer.GetProperty("scannedAt").GetString()!;
            text.Should().EndWith("Z", "an ISO time in UTC");
            var scannedAt = answer.GetProperty("scannedAt").GetDateTime();
            scannedAt.Kind.Should().Be(DateTimeKind.Utc);
            scannedAt.Should().BeAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        }
        answers.Select(a => a.GetProperty("scannedAt").GetString()).Distinct().Should().ContainSingle("all came from one scan");
        (await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects", @in = Newer }))
            .TryGetProperty("scannedAt", out _).Should().BeFalse("a version listing needs no scan");
    }

    [Fact]
    public async Task Rescan_sees_what_changed_in_the_source_since_the_cached_scan()
    {
        var (service, _) = Create();
        var first = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects" });
        WriteFile(_source, "late.txt", "late");

        var cached = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects" });
        var rescanned = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects", rescan = true });

        Names(cached).Should().NotContain("late.txt", "without rescan the cached scan answers");
        cached.GetProperty("scannedAt").GetString().Should().Be(first.GetProperty("scannedAt").GetString());
        Names(rescanned).Should().Contain("late.txt");
        rescanned.GetProperty("scannedAt").GetDateTime().Should().BeAfter(first.GetProperty("scannedAt").GetDateTime());
        foreach (var op in new[] { ControlOps.ExplainPath, ControlOps.GetIgnoreRules, ControlOps.PreviewIgnoreChange, ControlOps.CompareSourceWithVersion })
        {
            object args = op switch
            {
                ControlOps.ExplainPath => new { plan = "Projects", path = "a.txt", rescan = true },
                ControlOps.PreviewIgnoreChange => new { plan = "Projects", add = new[] { "*.md" }, rescan = true },
                _ => new { plan = "Projects", rescan = true },
            };
            var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
            (response.Error?.Key ?? "ok").Should().NotBe(ControlErrors.BadRequest, op + " takes 'rescan'");
        }
        (await FailAsync(service, ControlOps.ListFiles, new { plan = "Projects", rescan = "yes" })).Key.Should().Be(ControlErrors.BadRequest);
    }

    [Fact]
    public async Task A_version_newer_than_the_scan_makes_the_next_answer_scan_again()
    {
        var (service, vm) = Create();
        var first = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects" });
        WriteFile(_source, "late.txt", "late");
        var name = VersionName.Format(DateTime.Now.AddMinutes(1), "Projects");
        vm.VersionIndexes.For("p1").Add(new VersionInfo(name, name, DateTime.Now.AddMinutes(1), VersionOwnership.Owned, null, null),
            new BackupManifest { Files = [] }, new StorageFactory().Open(StorageLocation.FileSystem(_target)));

        var after = await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects" });

        Names(after).Should().Contain("late.txt", "the scan was older than the newest version");
        after.GetProperty("scannedAt").GetDateTime().Should().BeAfter(first.GetProperty("scannedAt").GetDateTime());
    }

    [Fact]
    public async Task A_finished_run_drops_the_plans_cached_scan()
    {
        var (service, vm) = Create();
        await OkAsync(service, ControlOps.ListFiles, new { plan = "Projects" });
        vm.ScanCache.Has("p1").Should().BeTrue();

        _fixture.Queue.Enqueue(new BackupRequest(vm.Plans.Single(p => p.Id == "p1").SavedPlan(), [], RunTrigger.Manual))
            .Should().BeTrue();
        await _fixture.Queue.WhenIdleAsync();

        vm.ScanCache.Has("p1").Should().BeFalse("the run made a version newer than the scan");
    }

    private static IEnumerable<string> Names(JsonElement listing) =>
        listing.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToList();

    // ------------------------------------------------------------------ get_run_history

    [Fact]
    public async Task Get_run_history_returns_the_newest_runs_first()
    {
        AppendRun("p1", new RunLogEntry
        {
            RunId = "r1", Trigger = RunTrigger.Manual, Status = RunStatus.Completed, Version = Older,
            StartUtc = Utc(2026, 9, 29, 12, 5), EndUtc = Utc(2026, 9, 29, 12, 6), DurationMs = 60_000,
            FilesCopied = 2, BytesCopied = 7,
        });
        AppendRun("p1", new RunLogEntry
        {
            RunId = "r2", Trigger = RunTrigger.Scheduled, Status = RunStatus.CompletedWithWarnings, Version = Newer,
            StartUtc = Utc(2026, 9, 30, 12, 5), EndUtc = Utc(2026, 9, 30, 12, 6), FilesCopied = 3, BytesCopied = 2048,
            SkippedCount = 2,
        });
        AppendRun("p1", new RunLogEntry
        {
            RunId = "r3", Trigger = RunTrigger.CatchUp, Status = RunStatus.Error, Reason = "The target is full.",
            StartUtc = Utc(2026, 10, 1, 12, 5), EndUtc = Utc(2026, 10, 1, 12, 5),
        });
        var (service, _) = Create();

        var result = await OkAsync(service, ControlOps.GetRunHistory, new { plan = "Projects", n = 2 });

        var runs = result.GetProperty("runs").EnumerateArray().ToList();
        runs.Select(r => r.GetProperty("runId").GetString()).Should().Equal("r3", "r2");
        runs[0].GetProperty("trigger").GetString().Should().Be("catchUp");
        runs[0].GetProperty("status").GetString().Should().Be("error");
        runs[0].GetProperty("reason").GetString().Should().Be("The target is full.");
        runs[0].TryGetProperty("version", out _).Should().BeFalse();
        runs[1].GetProperty("status").GetString().Should().Be("completedWithWarnings");
        runs[1].GetProperty("version").GetString().Should().Be(Newer);
        runs[1].GetProperty("files").GetInt32().Should().Be(3);
        runs[1].GetProperty("size").GetProperty("text").GetString().Should().Be("2.0 KB");
        runs[1].GetProperty("skipped").GetInt32().Should().Be(2);
        runs[1].GetProperty("start").GetDateTime().Should().Be(Utc(2026, 9, 30, 12, 5));
        result.GetProperty("total").GetInt32().Should().Be(3);
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("hint").GetString().Should().Contain("n");
    }

    // ------------------------------------------------------------------ metadata only

    [Fact]
    public async Task No_result_contains_file_content()
    {
        AppendRun("p1", new RunLogEntry { RunId = "r1", Status = RunStatus.Completed, Version = Newer });
        var (service, _) = await CreateWithVersionsAsync();
        var calls = new List<(string Op, object Args)>
        {
            (ControlOps.ListPlans, new { }),
            (ControlOps.ListVersions, new { plan = "p1" }),
            (ControlOps.GetIgnoreRules, new { plan = "p1" }),
            (ControlOps.GetRunHistory, new { plan = "p1" }),
            (ControlOps.ListFiles, new { plan = "p1", path = "" }),
            (ControlOps.ListFiles, new { plan = "p1", path = "docs" }),
            (ControlOps.ListFiles, new { plan = "p1", path = "logs" }),
            (ControlOps.ListFiles, new { plan = "p1", @in = Newer }),
            (ControlOps.ListFiles, new { plan = "p1", path = "sub", @in = Newer }),
        };
        foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories))
            calls.Add((ControlOps.ExplainPath, new { plan = "p1", path = Path.GetRelativePath(_source, file) }));

        foreach (var (op, args) in calls)
        {
            var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
            response.Ok.Should().BeTrue($"{op} {JsonSerializer.Serialize(args)}: {response.Error?.Message}");
            JsonSerializer.Serialize(response, ControlJson.Options).Should().NotContain(Secret, $"{op} returns metadata only");
        }
    }

    // ------------------------------------------------------------------ the character budget

    [Fact]
    public async Task Huge_ignore_file_stays_within_the_character_budget()
    {
        var lines = Enumerable.Range(0, 10_000).Select(i => $"generated/pattern-number-{i:00000}/**/*.cache");
        WriteFile(_source, "huge/.backupignore", string.Join('\n', lines));
        var (service, _) = Create();

        var response = await service.HandleAsync(Request(ControlOps.GetIgnoreRules, new { plan = "Projects" }), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error?.Message);
        JsonSerializer.Serialize(response, ControlJson.Options).Length.Should().BeLessThanOrEqualTo(ControlService.MaxChars);
        var result = response.Result!.Value;
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("hint").GetString().Should().Contain("path");
        var huge = result.GetProperty("ignoreFiles").EnumerateArray().Single(f => f.GetProperty("path").GetString() == "huge/.backupignore");
        huge.GetProperty("truncated").GetBoolean().Should().BeTrue();
        huge.GetProperty("lineCount").GetInt32().Should().Be(10_000);
        huge.GetProperty("lines").GetArrayLength().Should().Be(ControlService.MaxRulesPerOrigin);
    }

    [Fact]
    public async Task Get_ignore_rules_path_lists_only_the_ignore_files_in_that_folder()
    {
        WriteFile(_source, "other/.backupignore", "*.o");
        var (service, _) = Create();

        var result = await OkAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects", path = "docs" });

        result.GetProperty("ignoreFiles").EnumerateArray().Select(f => f.GetProperty("path").GetString())
            .Should().Equal("docs/.backupignore");
        result.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Two_hundred_bulky_runs_stay_within_the_character_budget()
    {
        for (var i = 0; i < 200; i++)
        {
            AppendRun("p1", new RunLogEntry
            {
                RunId = $"run-{i:000}", Trigger = RunTrigger.Scheduled, Status = RunStatus.Error,
                Reason = new string('r', 300), StartUtc = Utc(2026, 9, 1, 0, 0).AddHours(i), EndUtc = Utc(2026, 9, 1, 0, 0).AddHours(i),
                Warnings = Enumerable.Range(0, 12).Select(w => $"Warning {w} of run {i}.").ToList(),
            });
        }
        var (service, _) = Create();

        var response = await service.HandleAsync(Request(ControlOps.GetRunHistory, new { plan = "Projects", n = 200 }), CancellationToken.None);

        response.Ok.Should().BeTrue(response.Error?.Message);
        JsonSerializer.Serialize(response, ControlJson.Options).Length.Should().BeLessThanOrEqualTo(ControlService.MaxChars);
        var result = response.Result!.Value;
        result.GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.GetProperty("total").GetInt32().Should().Be(200);
        result.GetProperty("hint").GetString().Should().Contain("'n'");
        var runs = result.GetProperty("runs").EnumerateArray().ToList();
        runs.Count.Should().BeInRange(1, 199);
        runs[0].GetProperty("runId").GetString().Should().Be("run-199", "the newest runs are kept");
        var warnings = runs[0].GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        warnings.Should().HaveCount(ControlService.MaxWarningsPerRow + 1);
        warnings[^1].Should().Be("… and 7 more warnings.");
    }

    // ------------------------------------------------------------------ robustness

    [Fact]
    public async Task Unreadable_run_log_does_not_fail_list_versions()
    {
        AppendRun("p1", new RunLogEntry { RunId = "r1", Status = RunStatus.Completed, Version = Newer });
        var (service, _) = await CreateWithVersionsAsync();
        using var locked = new FileStream(_fixture.Paths.LogFileFor("p1"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = await OkAsync(service, ControlOps.ListVersions, new { plan = "Projects" });

        result.GetProperty("versions").GetArrayLength().Should().Be(3);
        (await FailAsync(service, ControlOps.GetRunHistory, new { plan = "Projects" })).Key.Should().Be(ControlErrors.InternalError);
    }

    [Fact]
    public async Task Canceled_request_does_not_wait_for_the_ui_thread()
    {
        var vm = _fixture.Create();
        var service = new ControlService(vm, new StalledContext());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var call = service.HandleAsync(Request(ControlOps.ListPlans, new { }), cts.Token);

        await FluentActions.Awaiting(() => call.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>A UI thread that never gets to what is posted to it (e.g. a modal dialog that does not end).</summary>
    private sealed class StalledContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) { }
    }

    [Fact]
    public async Task Unknown_op_is_a_bad_request()
    {
        var (service, _) = Create();

        var error = await FailAsync(service, "format_disk", new { });

        error.Key.Should().Be(ControlErrors.BadRequest);
        error.Message.Should().Contain("format_disk");
    }

    // ------------------------------------------------------------------ helpers

    private (ControlService Service, MainViewModel Vm) Create()
    {
        var vm = _fixture.Create();
        return (new ControlService(vm, _ui), vm);
    }

    /// <summary>
    /// Two versions in the target, indexed as the Versions tab indexes them; then a third one the index has not seen.
    /// </summary>
    private async Task<(ControlService Service, MainViewModel Vm)> CreateWithVersionsAsync()
    {
        WriteFile(_target, Older + "/a.txt", "alpha");
        WriteFile(_target, Older + "/gone.txt", "xx");
        WriteFile(_target, Newer + "/a.txt", "alpha-longer");
        WriteFile(_target, Newer + "/new.txt", "abc");
        WriteFile(_target, Newer + "/sub/deep.txt", Secret + "-v");
        var (service, vm) = Create();
        await vm.Plans.Single(p => p.Id == "p1").Versions.RefreshCommand.ExecuteAsync(null);
        WriteFile(_target, Unindexed + "/z.txt", "z");
        return (service, vm);
    }

    private static ControlRequest Request(string op, object args) =>
        new("1", op, JsonSerializer.SerializeToElement(args, ControlJson.Options));

    private static async Task<JsonElement> OkAsync(ControlService service, string op, object args)
    {
        var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
        response.Ok.Should().BeTrue(response.Error?.Message);
        response.Id.Should().Be("1");
        return response.Result!.Value;
    }

    private static async Task<ControlError> FailAsync(ControlService service, string op, object args)
    {
        var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
        response.Ok.Should().BeFalse();
        return response.Error!;
    }

    private static IEnumerable<(int Line, string Pattern, int Entries, long Bytes)> Hits(JsonElement lines) =>
        lines.EnumerateArray().Select(l => (l.GetProperty("line").GetInt32(), l.GetProperty("pattern").GetString()!,
            l.GetProperty("entries").GetInt32(), l.GetProperty("size").GetProperty("bytes").GetInt64())).ToList();

    private static (string Pattern, string Origin, int Line, bool Negated) Rule(JsonElement entry)
    {
        var rule = entry.GetProperty("rule");
        return (rule.GetProperty("pattern").GetString()!, rule.GetProperty("origin").GetString()!,
            rule.GetProperty("line").GetInt32(), rule.GetProperty("negated").GetBoolean());
    }

    private void AppendRun(string planId, RunLogEntry entry) => new RunLog(_fixture.Paths.LogFileFor(planId)).Append(entry);

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Opens folders on disk; the listing of <paramref name="heldPath"/> waits for <paramref name="gate"/>.</summary>
    private sealed class HoldingFactory(string heldPath, Task gate) : IStorageFactory
    {
        private readonly StorageFactory _inner = new();

        public IStorage Open(StorageLocation location)
        {
            var storage = _inner.Open(location);
            return string.Equals(location.Path, heldPath, StringComparison.OrdinalIgnoreCase)
                ? new HoldingStorage(storage, gate)
                : storage;
        }
    }

    private sealed class HoldingStorage(IStorage inner, Task gate) : IStorage
    {
        public StorageCapabilities Capabilities => inner.Capabilities;

        public Task<StorageEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public async IAsyncEnumerable<StorageEntry> ListAsync(string folder, bool recursive,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            await foreach (var entry in inner.ListAsync(folder, recursive, ct))
                yield return entry;
        }

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);

        public Task<StorageWriter> CreateAsync(string path, CreateOptions options, CancellationToken ct) =>
            inner.CreateAsync(path, options, ct);

        public Task DeleteAsync(IReadOnlyList<string> paths, CancellationToken ct) => inner.DeleteAsync(paths, ct);

        public Task EnsureDirectoryAsync(string path, CancellationToken ct) => inner.EnsureDirectoryAsync(path, ct);

        public Task<long?> GetFreeSpaceAsync(CancellationToken ct) => inner.GetFreeSpaceAsync(ct);
    }
}
