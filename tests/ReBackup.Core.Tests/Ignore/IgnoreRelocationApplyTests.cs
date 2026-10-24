using System.Text;
using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage.FileSystem;

namespace ReBackup.Core.Tests.Ignore;

public class IgnoreRelocationApplyTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private sealed class FakeBin(List<string>? order = null) : IRecycleBin
    {
        public List<string> Recycled { get; } = [];
        public Exception? Fail { get; set; }

        public RecycleOutcome Recycle(string path)
        {
            order?.Add("recycle");
            if (Fail is not null)
                throw Fail;
            Recycled.Add(path);
            return RecycleOutcome.Recycled;
        }
    }

    private async Task<RelocationContext> ContextAsync(IEnumerable<string> plan)
    {
        var settings = new IgnoreSettings { UseGlobalDefaults = false, HonorNestedFiles = true, Patterns = [.. plan] };
        var index = await SourceIndexer.BuildAsync(new FileSystemStorage(_tmp.Root));
        return new RelocationContext(settings, [], index, _tmp.Root);
    }

    private string FilePath(string dir) => _tmp.PathOf(Path.Combine(dir, ".backupignore"));

    [Fact]
    public async Task Apply_writes_the_file_and_saves_the_patterns()
    {
        _tmp.WriteFile("P/a.txt", "1");
        var context = await ContextAsync(["/P/**/foo/", "bar/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");
        IReadOnlyList<string>? saved = null;

        IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/", "bar/"], p => saved = p, new FakeBin());

        File.ReadAllText(FilePath("P")).Should().Be(proposal.FileContentAfter);
        saved.Should().Equal(proposal.PlanPatternsAfter);
        Directory.GetFiles(_tmp.PathOf("P")).Should().HaveCount(2, "no temp file is left behind");
    }

    [Fact]
    public async Task Apply_extract_does_not_recycle()
    {
        _tmp.WriteFile("P/a.txt", "1");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");
        var bin = new FakeBin();

        IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"], _ => { }, bin);

        bin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_integrate_recycles_the_file_after_saving()
    {
        _tmp.WriteFile("P/.backupignore", "foo/\n");
        var context = await ContextAsync([]);
        var proposal = IgnoreRelocation.ProposeIntegrate(context, "P");
        var order = new List<string>();
        var bin = new FakeBin(order);
        IReadOnlyList<string>? saved = null;

        IgnoreRelocation.Apply(proposal, _tmp.Root, [], p => { saved = p; order.Add("save"); }, bin);

        saved.Should().Equal(proposal.PlanPatternsAfter);
        bin.Recycled.Should().Equal(FilePath("P"));
        order.Should().Equal("save", "recycle");
    }

    [Fact]
    public async Task Apply_fails_when_the_plan_changed()
    {
        _tmp.WriteFile("P/a.txt", "1");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");
        var saves = 0;

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/", "extra/"], _ => saves++, new FakeBin());

        act.Should().Throw<RelocationStaleException>().Which.Message.Should().Be("ignore.relocation.stale");
        saves.Should().Be(0);
        File.Exists(FilePath("P")).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_fails_when_the_file_changed()
    {
        _tmp.WriteFile("P/.backupignore", "old/\n");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");
        File.WriteAllText(FilePath("P"), "edited/\n");
        var saves = 0;

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"], _ => saves++, new FakeBin());

        act.Should().Throw<RelocationStaleException>();
        saves.Should().Be(0);
        File.ReadAllText(FilePath("P")).Should().Be("edited/\n");
    }

    [Fact]
    public async Task Apply_fails_when_a_missing_file_appeared()
    {
        _tmp.WriteFile("P/a.txt", "1");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");
        File.WriteAllText(FilePath("P"), "new/\n");

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"], _ => { }, new FakeBin());

        act.Should().Throw<RelocationStaleException>();
        File.ReadAllText(FilePath("P")).Should().Be("new/\n");
    }

    [Fact]
    public async Task Apply_restores_the_file_when_saving_the_plan_fails()
    {
        _tmp.WriteFile("P/.backupignore", "old/\n");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"],
            _ => throw new IOException("disk full"), new FakeBin());

        act.Should().Throw<IOException>().WithMessage("disk full");
        File.ReadAllText(FilePath("P")).Should().Be(proposal.FileContentBefore);
    }

    [Fact]
    public async Task Apply_removes_a_newly_created_file_when_saving_the_plan_fails()
    {
        _tmp.WriteFile("P/a.txt", "1");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"], _ => throw new IOException("x"), new FakeBin());

        act.Should().Throw<IOException>();
        File.Exists(FilePath("P")).Should().BeFalse();
    }

    [Fact]
    public async Task Apply_integrate_keeps_the_file_when_saving_the_plan_fails()
    {
        _tmp.WriteFile("P/.backupignore", "foo/\n");
        var context = await ContextAsync([]);
        var proposal = IgnoreRelocation.ProposeIntegrate(context, "P");
        var bin = new FakeBin();

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, [], _ => throw new IOException("x"), bin);

        act.Should().Throw<IOException>();
        File.ReadAllText(FilePath("P")).Should().Be("foo/\n");
        bin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_integrate_restores_the_plan_when_recycling_fails()
    {
        _tmp.WriteFile("P/.backupignore", "foo/\n");
        var context = await ContextAsync(["bin/"]);
        var proposal = IgnoreRelocation.ProposeIntegrate(context, "P");
        var saves = new List<IReadOnlyList<string>>();

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, ["bin/"], saves.Add,
            new FakeBin { Fail = new IOException("recycle failed") });

        act.Should().Throw<IOException>().WithMessage("recycle failed");
        saves.Should().HaveCount(2);
        saves[0].Should().Equal(proposal.PlanPatternsAfter);
        saves[1].Should().Equal(proposal.PlanPatternsBefore);
    }

    [Fact]
    public void Apply_of_a_proposal_without_moved_lines_throws_before_touching_anything()
    {
        var proposal = new RelocationProposal(RelocationKind.Integrate, "P", [], [], null, null, [],
            new EquivalenceResult.Identical(), IgnoreRelocation.Hash([]), null);
        var saves = 0;
        var bin = new FakeBin();

        var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, [], _ => saves++, bin);

        act.Should().Throw<InvalidOperationException>();
        saves.Should().Be(0);
        bin.Recycled.Should().BeEmpty();
    }

    [Fact]
    public async Task Crlf_file_is_not_stale_and_keeps_crlf()
    {
        _tmp.WriteFile("P/.backupignore", "a/\r\n");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");

        IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"], _ => { }, new FakeBin());

        File.ReadAllText(FilePath("P")).Should().Be("a/\r\n# from plan\r\nfoo/\r\n");
    }

    [Fact]
    public async Task Bom_is_ignored_for_the_hash()
    {
        _tmp.WriteFile("P/.backupignore", "a/\n");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");
        File.WriteAllBytes(FilePath("P"), [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("a/\n")]);

        IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"], _ => { }, new FakeBin());

        File.ReadAllText(FilePath("P")).Should().Be("a/\n# from plan\nfoo/\n");
    }

    [Fact]
    public async Task Apply_returns_the_recycle_outcome_for_integrate_and_null_for_extract()
    {
        _tmp.WriteFile("P/.backupignore", "foo/\n");
        var context = await ContextAsync([]);
        var integrate = IgnoreRelocation.ProposeIntegrate(context, "P");
        IgnoreRelocation.Apply(integrate, _tmp.Root, [], _ => { }, new FakeBin()).Should().Be(RecycleOutcome.Recycled);

        _tmp.WriteFile("Q/a.txt", "1");
        context = await ContextAsync(["/Q/**/foo/"]);
        var extract = IgnoreRelocation.ProposeExtract(context, [0], "Q");
        IgnoreRelocation.Apply(extract, _tmp.Root, ["/Q/**/foo/"], _ => { }, new FakeBin()).Should().BeNull();
    }

    [Fact]
    public async Task Apply_leaves_everything_untouched_when_the_file_cannot_be_written()
    {
        var path = _tmp.WriteFile("P/.backupignore", "old/\n");
        var context = await ContextAsync(["/P/**/foo/"]);
        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P");
        var saves = 0;
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var act = () => IgnoreRelocation.Apply(proposal, _tmp.Root, ["/P/**/foo/"], _ => saves++, new FakeBin());

            act.Should().Throw<Exception>();
            saves.Should().Be(0);
            File.ReadAllText(path).Should().Be("old/\n");
            Directory.GetFiles(_tmp.PathOf("P")).Should().ContainSingle();
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void IntegratedFileName_picks_the_first_free_name()
    {
        var dir = _tmp.CreateDir("P");
        IgnoreRelocation.IntegratedFileName(dir).Should().Be(Path.Combine(dir, ".backupignore.integrated"));

        _tmp.WriteFile("P/.backupignore.integrated", "x");
        IgnoreRelocation.IntegratedFileName(dir).Should().Be(Path.Combine(dir, ".backupignore.integrated-2"));

        _tmp.WriteFile("P/.backupignore.integrated-2", "x");
        IgnoreRelocation.IntegratedFileName(dir).Should().Be(Path.Combine(dir, ".backupignore.integrated-3"));
    }
}
