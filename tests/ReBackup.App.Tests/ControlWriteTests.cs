using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using ReBackup.App.Control;
using ReBackup.App.Tests.TestSupport;
using ReBackup.App.ViewModels;
using ReBackup.Control;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Shared.Schedule;
using ReBackup.Storage;

namespace ReBackup.App.Tests;

/// <summary>
/// The control service's writes: ignore-change and relocation proposals, applying them to the saved plan, starting a
/// backup and following its run. Unsaved edits in the app are never overwritten.
/// </summary>
public class ControlWriteTests : IDisposable
{
    private readonly MainViewModelFixture _fixture = new();
    private readonly SerialContext _ui = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly List<(string Title, string Message)> _notifications = [];
    private readonly string _source;
    private readonly string _target;

    public ControlWriteTests()
    {
        _source = Path.Combine(_fixture.Root, "source");
        _target = Path.Combine(_fixture.Root, "target");
        WriteFile(_source, "a.txt", "alpha-alpha-15b");          // 15 bytes
        WriteFile(_source, "logs/x.log", "log-one-15bytes");     // 15
        WriteFile(_source, "logs/y.log", "log-two-16-bytes");    // 16
        WriteFile(_source, "docs/readme.md", "readme-16-bytes!"); // 16
        WriteFile(_source, "docs/notes.md", "notes-12byte");      // 12
        WriteFile(_source, "docs/scratch.tmp", "tmp-11bytes");   // 11
        WriteFile(_source, "docs/.backupignore", "*.tmp\n");     // 6
        Directory.CreateDirectory(_target);

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

    // ------------------------------------------------------------------ preview_ignore_change

    [Fact]
    public async Task Preview_counts_newly_ignored_and_included()
    {
        var (service, _) = Create();

        var result = await OkAsync(service, ControlOps.PreviewIgnoreChange,
            new { plan = "Projects", add = new[] { "*.md" }, remove = new[] { "logs/" } });

        result.GetProperty("proposalId").GetString().Should().NotBeNullOrEmpty();
        var ignored = result.GetProperty("newlyIgnored");
        ignored.GetProperty("files").GetInt32().Should().Be(2);
        ignored.GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(28);
        Examples(ignored).Should().BeEquivalentTo([("docs/readme.md", "file", 1, 16L), ("docs/notes.md", "file", 1, 12L)],
            options => options.WithStrictOrdering(), "the largest first");
        var included = result.GetProperty("newlyIncluded");
        included.GetProperty("files").GetInt32().Should().Be(2);
        included.GetProperty("size").GetProperty("bytes").GetInt64().Should().Be(31);
        Examples(included).Should().Equal([("logs/", "folder", 2, 31L)], "a whole folder is one example");
        result.GetProperty("planPatternsAfter").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("# build output", "*.md");
        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(["# build output", "logs/"], "a preview writes nothing");
    }

    [Fact]
    public async Task Preview_with_an_invalid_pattern_is_invalid_pattern()
    {
        var (service, _) = Create();

        var error = await FailAsync(service, ControlOps.PreviewIgnoreChange, new { plan = "Projects", add = new[] { "*.md", "!" } });

        error.Key.Should().Be(ControlErrors.InvalidPattern);
        error.Message.Should().Contain("'!'");
    }

    [Fact]
    public async Task Preview_removing_a_line_the_plan_lacks_is_a_bad_request()
    {
        var (service, _) = Create();

        var error = await FailAsync(service, ControlOps.PreviewIgnoreChange, new { plan = "Projects", remove = new[] { "bin/" } });

        error.Key.Should().Be(ControlErrors.BadRequest);
        error.Message.Should().Contain("bin/");
    }

    // ------------------------------------------------------------------ apply_change

    [Fact]
    public async Task Apply_ignore_change_saves_the_plan_and_the_editor_shows_it()
    {
        var (service, vm) = Create();
        var editor = vm.Plans.Single();
        var id = await PreviewAsync(service, add: ["*.md"]);

        var result = await OkAsync(service, ControlOps.ApplyChange, new { proposalId = id });

        result.GetProperty("applied").GetString().Should().Be("ignoreChange");
        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(["# build output", "logs/", "*.md"]);
        var shown = await OnUi(() => (editor.IsDirty, editor.CurrentIgnoreSettings().Patterns.ToList(), editor.SavedPlan().Ignore.Patterns.ToList()));
        shown.IsDirty.Should().BeFalse();
        shown.Item2.Should().Equal(["# build output", "logs/", "*.md"]);
        shown.Item3.Should().Equal(["# build output", "logs/", "*.md"]);
    }

    [Fact]
    public async Task Apply_twice_is_unknown_the_second_time()
    {
        var (service, _) = Create();
        var id = await PreviewAsync(service, add: ["*.md"]);
        await OkAsync(service, ControlOps.ApplyChange, new { proposalId = id });

        var error = await FailAsync(service, ControlOps.ApplyChange, new { proposalId = id });

        error.Key.Should().Be(ControlErrors.ProposalUnknown);
        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(["# build output", "logs/", "*.md"], "applied once");
    }

    [Fact]
    public async Task Expired_proposal_is_rejected()
    {
        var (service, _) = Create();
        var id = await PreviewAsync(service, add: ["*.md"]);
        _time.Advance(ProposalStore.Lifetime + TimeSpan.FromSeconds(1));

        var error = await FailAsync(service, ControlOps.ApplyChange, new { proposalId = id });

        error.Key.Should().Be(ControlErrors.ProposalExpired);
        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(["# build output", "logs/"]);
    }

    [Fact]
    public async Task Proposal_within_its_lifetime_applies()
    {
        var (service, _) = Create();
        var id = await PreviewAsync(service, add: ["*.md"]);
        _time.Advance(ProposalStore.Lifetime - TimeSpan.FromSeconds(1));

        await OkAsync(service, ControlOps.ApplyChange, new { proposalId = id });
    }

    [Fact]
    public async Task Apply_refuses_when_the_editor_has_unsaved_edits()
    {
        var (service, vm) = Create();
        var editor = vm.Plans.Single();
        var id = await PreviewAsync(service, add: ["*.md"]);
        await OnUi(() => editor.IgnorePatternsText = "# my own edit\nlogs/");

        var error = await FailAsync(service, ControlOps.ApplyChange, new { proposalId = id });

        error.Key.Should().Be(ControlErrors.ProposalStale);
        error.Message.Should().Contain("unsaved").And.Contain("save or revert");
        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(["# build output", "logs/"]);
        var shown = await OnUi(() => (editor.IsDirty, editor.IgnorePatternsText));
        shown.IsDirty.Should().BeTrue();
        shown.IgnorePatternsText.Should().Be("# my own edit\nlogs/", "the user's edit stays");
    }

    [Fact]
    public async Task Apply_after_the_patterns_were_saved_otherwise_is_stale()
    {
        var (service, vm) = Create();
        var editor = vm.Plans.Single();
        var id = await PreviewAsync(service, add: ["*.md"]);
        await OnUi(() =>
        {
            editor.IgnorePatternsText = "logs/";
            vm.SaveCommand.Execute(null);
            return true;
        });

        var error = await FailAsync(service, ControlOps.ApplyChange, new { proposalId = id });

        error.Key.Should().Be(ControlErrors.ProposalStale);
        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(["logs/"]);
    }

    // ------------------------------------------------------------------ propose_relocation

    [Fact]
    public async Task Relocation_through_control_matches_the_dialog_result()
    {
        // The dialog, in a second configuration with the same source layout.
        using var dialogFixture = new MainViewModelFixture();
        var dialogSource = Path.Combine(dialogFixture.Root, "source");
        foreach (var file in Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories))
            WriteFile(dialogSource, Path.GetRelativePath(_source, file), File.ReadAllText(file));
        dialogFixture.Store.Save(new BackupPlan
        {
            Id = "p1", Name = "Projects", Source = StorageLocation.FileSystem(dialogSource),
            Target = StorageLocation.FileSystem(Path.Combine(dialogFixture.Root, "target")),
            Ignore = new IgnoreSettings { Patterns = ["# build output", "logs/"] },
        });
        var dialogEditor = dialogFixture.Create().Plans.Single();
        await dialogEditor.Preview.IndexCommand.ExecuteAsync(null);
        dialogEditor.Preview.ShowIgnoreFilesOnly = true;
        dialogEditor.Preview.SelectedIgnoreFileItem = dialogEditor.Preview.IgnoreFileItems.Single(i => i.Directory == "docs");
        dialogEditor.IntegrateIgnoreFileCommand.Execute(null);
        var dialogProposal = dialogFixture.Dialogs.Relocations.Single().Proposal;

        // The control service.
        var (service, _) = Create();
        var proposed = await OkAsync(service, ControlOps.ProposeRelocation, new { plan = "Projects", integrate = "docs/.backupignore" });
        proposed.GetProperty("kind").GetString().Should().Be("integrate");
        proposed.GetProperty("equivalence").GetProperty("state").GetString().Should().Be("identical");
        proposed.GetProperty("planLinesAdded").EnumerateArray().Select(e => e.GetString()).Should().Equal(dialogProposal.MovedLines);
        await OkAsync(service, ControlOps.ApplyChange, new { proposalId = proposed.GetProperty("proposalId").GetString() });

        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(dialogFixture.Store.TryLoad("p1")!.Ignore.Patterns);
        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(dialogProposal.PlanPatternsAfter);
        File.Exists(Path.Combine(_source, "docs", ".backupignore")).Should().BeFalse();
        File.Exists(Path.Combine(dialogSource, "docs", ".backupignore")).Should().BeFalse();
        _fixture.RecycleBin.Recycled.Should().Equal(Path.Combine(_source, "docs", ".backupignore"));

        // The scan the control service reads from knows the file is gone: the rules come from the plan now.
        var rules = await OkAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects" });
        rules.GetProperty("ignoreFiles").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_relocation_in_the_dialog_reaches_the_scan_the_control_service_reads()
    {
        var (service, vm) = Create();
        var editor = vm.Plans.Single();
        await editor.Preview.IndexCommand.ExecuteAsync(null);
        (await OkAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects" })).GetProperty("ignoreFiles").GetArrayLength().Should().Be(1);
        editor.Preview.ShowIgnoreFilesOnly = true;
        editor.Preview.SelectedIgnoreFileItem = editor.Preview.IgnoreFileItems.Single(i => i.Directory == "docs");

        editor.IntegrateIgnoreFileCommand.Execute(null);

        _fixture.Dialogs.Relocations.Should().ContainSingle();
        var rules = await OkAsync(service, ControlOps.GetIgnoreRules, new { plan = "Projects" });
        rules.GetProperty("ignoreFiles").GetArrayLength().Should().Be(0, "the cached scan no longer has the integrated file");
        rules.GetProperty("planPatterns").EnumerateArray().Select(l => l.GetProperty("pattern").GetString()).Should().Contain("/docs/**/*.tmp");
    }

    [Fact]
    public async Task Extract_relocation_writes_the_folder_file_and_saves_the_plan()
    {
        var plan = _fixture.Store.TryLoad("p1")!;
        plan.Ignore.Patterns = ["logs/", "/docs/*.md"];
        _fixture.Store.Save(plan);
        var (service, _) = Create();

        var proposed = await OkAsync(service, ControlOps.ProposeRelocation,
            new { plan = "Projects", extract = new[] { "/docs/*.md" }, folder = "docs" });
        proposed.GetProperty("kind").GetString().Should().Be("extract");
        proposed.GetProperty("planLinesRemoved").EnumerateArray().Select(e => e.GetString()).Should().Equal("/docs/*.md");
        await OkAsync(service, ControlOps.ApplyChange, new { proposalId = proposed.GetProperty("proposalId").GetString() });

        _fixture.Store.TryLoad("p1")!.Ignore.Patterns.Should().Equal(["logs/"]);
        File.ReadAllText(Path.Combine(_source, "docs", ".backupignore")).Should().Contain("*.md");
    }

    [Fact]
    public async Task Relocation_on_a_storage_source_is_source_not_folder()
    {
        _fixture.Store.Save(new BackupPlan
        {
            Id = "s3", Name = "Cloud", Source = new StorageLocation("s3", "photos", "conn-1"),
            Target = StorageLocation.FileSystem(_target),
        });
        var (service, _) = Create();

        var error = await FailAsync(service, ControlOps.ProposeRelocation, new { plan = "Cloud", integrate = ".backupignore" });

        error.Key.Should().Be(ControlErrors.SourceNotFolder);
    }

    // ------------------------------------------------------------------ start_backup / get_run_status

    [Fact]
    public async Task Start_backup_enqueues_with_trigger_Mcp()
    {
        var (service, vm) = Create();
        var editor = vm.Plans.Single();
        await OnUi(() => editor.IgnorePatternsText = "logs/\n*.md");   // unsaved: the run uses the saved plan

        var result = await OkAsync(service, ControlOps.StartBackup, new { plan = "Projects" });
        await _fixture.Queue.WhenIdleAsync();

        var runId = result.GetProperty("runId").GetString()!;
        runId.Should().StartWith("p1:");
        long.TryParse(runId["p1:".Length..], out _).Should().BeTrue();
        var run = new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll().Should().ContainSingle().Subject;
        run.Trigger.Should().Be(RunTrigger.Mcp);
        run.Status.Should().Be(RunStatus.Completed);
        Directory.EnumerateFiles(_target, "readme.md", SearchOption.AllDirectories).Should().NotBeEmpty("the saved plan does not ignore *.md");
        (await OnUi(() => (editor.IsDirty, editor.IgnorePatternsText))).Should().Be((true, "logs/\n*.md"), "the edits are untouched");

        var status = await OkAsync(service, ControlOps.GetRunStatus, new { runId });
        status.GetProperty("state").GetString().Should().Be("finished");
        status.GetProperty("result").GetProperty("status").GetString().Should().Be("completed");
        status.GetProperty("result").GetProperty("trigger").GetString().Should().Be("mcp");
    }

    [Fact]
    public async Task Start_backup_while_running_is_plan_running()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fixture.Storages = new HoldingFactory(_source, gate.Task);
        var (service, _) = Create();
        var first = await OkAsync(service, ControlOps.StartBackup, new { plan = "Projects" });
        try
        {
            var error = await FailAsync(service, ControlOps.StartBackup, new { plan = "Projects" });
            error.Key.Should().Be(ControlErrors.PlanRunning);

            var status = await OkAsync(service, ControlOps.GetRunStatus, new { runId = first.GetProperty("runId").GetString() });
            status.GetProperty("state").GetString().Should().BeOneOf("queued", "running");
        }
        finally
        {
            gate.TrySetResult();
            await _fixture.Queue.WhenIdleAsync();
        }
        new RunLog(_fixture.Paths.LogFileFor("p1")).ReadAll().Should().ContainSingle("the second start queued nothing");
    }

    [Fact]
    public async Task Run_status_of_a_run_that_never_started_is_unknown()
    {
        var (service, _) = Create();

        var status = await OkAsync(service, ControlOps.GetRunStatus, new { runId = "p1:" + DateTime.UtcNow.Ticks });
        status.GetProperty("state").GetString().Should().Be("unknown");

        (await FailAsync(service, ControlOps.GetRunStatus, new { runId = "garbage" })).Key.Should().Be(ControlErrors.BadRequest);
    }

    // ------------------------------------------------------------------ notifications

    [Fact]
    public async Task Writes_raise_a_notification()
    {
        var (service, _) = Create();
        var id = await PreviewAsync(service, add: ["*.md"]);
        _notifications.Should().BeEmpty("a proposal changes nothing");

        await OkAsync(service, ControlOps.ApplyChange, new { proposalId = id });
        await OkAsync(service, ControlOps.StartBackup, new { plan = "Projects" });
        await _fixture.Queue.WhenIdleAsync();

        lock (_notifications)
        {
            _notifications.Select(n => n.Message).Should().Equal(
                "Claude changed the ignore rules of \"Projects\".",
                "Claude started a backup of \"Projects\".");
            _notifications.Should().OnlyContain(n => n.Title.Length > 0);
        }
    }

    // ------------------------------------------------------------------ helpers

    private (ControlService Service, MainViewModel Vm) Create()
    {
        var vm = _fixture.Create();
        vm.ControlNotified += (title, message) =>
        {
            lock (_notifications)
                _notifications.Add((title, message));
        };
        return (new ControlService(vm, _ui, new ProposalStore(_time)), vm);
    }

    private static async Task<string> PreviewAsync(ControlService service, string[]? add = null, string[]? remove = null)
    {
        var result = await OkAsync(service, ControlOps.PreviewIgnoreChange,
            new { plan = "Projects", add = add ?? [], remove = remove ?? [] });
        return result.GetProperty("proposalId").GetString()!;
    }

    private Task<T> OnUi<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try { done.SetResult(action()); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    private static IReadOnlyList<(string Path, string Kind, int Files, long Bytes)> Examples(JsonElement side) =>
        side.GetProperty("examples").EnumerateArray().Select(e => (e.GetProperty("path").GetString()!,
            e.GetProperty("kind").GetString()!, e.GetProperty("files").GetInt32(),
            e.GetProperty("size").GetProperty("bytes").GetInt64())).ToList();

    private static ControlRequest Request(string op, object args) =>
        new("1", op, JsonSerializer.SerializeToElement(args, ControlJson.Options));

    private static async Task<JsonElement> OkAsync(ControlService service, string op, object args)
    {
        var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
        response.Ok.Should().BeTrue(response.Error?.Message);
        return response.Result!.Value;
    }

    private static async Task<ControlError> FailAsync(ControlService service, string op, object args)
    {
        var response = await service.HandleAsync(Request(op, args), CancellationToken.None);
        response.Ok.Should().BeFalse();
        return response.Error!;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>Opens folders on disk; listing <paramref name="heldPath"/> waits for <paramref name="gate"/>.</summary>
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
