using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Storage.FileSystem;

namespace ReBackup.Core.Tests.Ignore;

public class IgnoreRelocationProposeTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private async Task<RelocationContext> ContextAsync(IEnumerable<string> plan, bool honorNested = true, bool scan = true)
    {
        var settings = new IgnoreSettings { UseGlobalDefaults = false, HonorNestedFiles = honorNested, Patterns = [.. plan] };
        var index = scan ? await SourceIndexer.BuildAsync(new FileSystemStorage(_tmp.Root)) : null;
        return new RelocationContext(settings, [], index, _tmp.Root);
    }

    [Fact]
    public async Task Integrate_moves_lines_to_the_end_of_the_plan_under_a_comment()
    {
        _tmp.WriteFile("P/X/.backupignore", "foo/\n");
        _tmp.WriteFile("P/X/a.txt", "1");
        var context = await ContextAsync(["bin/"]);

        var proposal = IgnoreRelocation.ProposeIntegrate(context, "P/X");

        proposal.Kind.Should().Be(RelocationKind.Integrate);
        proposal.Directory.Should().Be("P/X");
        proposal.PlanPatternsBefore.Should().Equal("bin/");
        proposal.PlanPatternsAfter.Should().Equal("bin/", "# from P/X/.backupignore", "/P/X/**/foo/");
        proposal.MovedLines.Should().Equal("# from P/X/.backupignore", "/P/X/**/foo/");
        proposal.FileContentBefore.Should().Be("foo/\n");
        proposal.FileContentAfter.Should().BeNull();
        proposal.PlanPatternsHash.Should().Be(IgnoreRelocation.Hash(["bin/"]));
        proposal.FileHash.Should().Be(IgnoreRelocation.HashContent("foo/\n"));
    }

    [Fact]
    public async Task Integrate_of_the_root_file_uses_the_short_comment()
    {
        _tmp.WriteFile(".backupignore", "foo/\n");
        var context = await ContextAsync([]);

        var proposal = IgnoreRelocation.ProposeIntegrate(context, "");

        proposal.PlanPatternsAfter.Should().Equal("# from .backupignore", "foo/");
    }

    [Fact]
    public async Task Integrate_of_a_plain_file_is_identical()
    {
        _tmp.WriteFile("P/X/.backupignore", "foo/\n*.tmp\n");
        _tmp.WriteFile("P/X/foo/a.txt", "1");
        _tmp.WriteFile("P/X/b.tmp", "1");
        _tmp.WriteFile("P/X/c.txt", "1");
        var context = await ContextAsync([]);

        IgnoreRelocation.ProposeIntegrate(context, "P/X").Equivalence.Should().BeOfType<EquivalenceResult.Identical>();
    }

    [Fact]
    public async Task Integrate_detects_a_precedence_change()
    {
        _tmp.WriteFile("P/.backupignore", "*.log\n");
        _tmp.WriteFile("P/X/.backupignore", "!keep.log\n");
        _tmp.WriteFile("P/X/keep.log", "1");
        var context = await ContextAsync([]);

        var differs = IgnoreRelocation.ProposeIntegrate(context, "P/X").Equivalence
            .Should().BeOfType<EquivalenceResult.Differs>().Subject;

        differs.Count.Should().Be(1);
        var example = differs.Examples.Single();
        example.Path.Should().Be("P/X/keep.log");
        example.IgnoredBefore.Should().BeFalse();
        example.IgnoredAfter.Should().BeTrue();
        example.DecidingPattern.Should().Be("*.log");
    }

    [Fact]
    public async Task Extract_detects_a_precedence_change()
    {
        _tmp.WriteFile("P/X/Y/keep.log", "1");
        var context = await ContextAsync(["/P/X/**/*.log", "!/P/X/Y/keep.log"]);

        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P/X");

        var differs = proposal.Equivalence.Should().BeOfType<EquivalenceResult.Differs>().Subject;
        differs.Count.Should().Be(1);
        var example = differs.Examples.Single();
        example.Path.Should().Be("P/X/Y/keep.log");
        example.IgnoredBefore.Should().BeFalse();
        example.IgnoredAfter.Should().BeTrue();
    }

    [Fact]
    public async Task Extract_of_a_safe_line_is_identical()
    {
        _tmp.WriteFile("P/X/a/foo/f.txt", "1");
        _tmp.WriteFile("P/X/b.txt", "1");
        var context = await ContextAsync(["/P/X/**/foo/"]);

        IgnoreRelocation.ProposeExtract(context, [0], "P/X").Equivalence.Should().BeOfType<EquivalenceResult.Identical>();
    }

    [Fact]
    public async Task Extract_appends_under_a_comment_and_removes_plan_lines()
    {
        _tmp.WriteFile("P/X/.backupignore", "a/");
        var context = await ContextAsync(["bin/", "/P/X/**/foo/"]);

        var proposal = IgnoreRelocation.ProposeExtract(context, [1], "P/X");

        proposal.Kind.Should().Be(RelocationKind.Extract);
        proposal.FileContentBefore.Should().Be("a/");
        proposal.FileContentAfter.Should().Be("a/\n# from plan\nfoo/\n");
        proposal.PlanPatternsAfter.Should().Equal("bin/");
        proposal.MovedLines.Should().Equal("/P/X/**/foo/");
        proposal.Rejected.Should().BeEmpty();
        proposal.FileHash.Should().Be(IgnoreRelocation.HashContent("a/"));
    }

    [Fact]
    public async Task Extract_creates_the_file_when_missing()
    {
        _tmp.CreateDir("P/X");
        var context = await ContextAsync(["/P/X/**/foo/"]);

        var proposal = IgnoreRelocation.ProposeExtract(context, [0], "P/X");

        proposal.FileContentBefore.Should().BeNull();
        proposal.FileHash.Should().BeNull();
        proposal.FileContentAfter.Should().Be("# from plan\nfoo/\n");
    }

    [Fact]
    public async Task Extract_lists_rejected_lines_and_keeps_them_in_the_plan()
    {
        _tmp.CreateDir("P/X");
        var context = await ContextAsync(["*.tmp", "/P/X/**/foo/"]);

        var proposal = IgnoreRelocation.ProposeExtract(context, [0, 1], "P/X");

        proposal.Rejected.Should().Equal(new RejectedLine("*.tmp", RejectReason.MatchesOutsideFolder));
        proposal.PlanPatternsAfter.Should().Equal("*.tmp");
        proposal.FileContentAfter.Should().Be("# from plan\nfoo/\n");
    }

    [Fact]
    public void ExtractableLines_preselects_translatable_lines_only()
    {
        var settings = new IgnoreSettings
        {
            Patterns = ["# note", "*.tmp", "/P/X/**/foo/", "", "/Other/x", "!/P/X/build/"],
        };

        IgnoreRelocation.ExtractableLines(settings, "P/X").Should().Equal(2, 5);
    }

    [Fact]
    public async Task Extract_without_HonorNestedFiles_throws_InvalidOperationException()
    {
        var context = await ContextAsync(["/P/X/**/foo/"], honorNested: false);

        var act = () => IgnoreRelocation.ProposeExtract(context, [0], "P/X");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Without_an_index_equivalence_is_NotChecked()
    {
        _tmp.WriteFile("P/X/.backupignore", "foo/\n");
        var context = await ContextAsync([], scan: false);

        IgnoreRelocation.ProposeIntegrate(context, "P/X").Equivalence
            .Should().Be(new EquivalenceResult.NotChecked("ignore.relocation.noScan"));
    }

    [Fact]
    public async Task Equivalence_examples_are_capped_at_20()
    {
        _tmp.WriteFile("P/.backupignore", "*.log\n");
        _tmp.WriteFile("P/X/.backupignore", "!*.log\n");
        for (var i = 0; i < 25; i++)
            _tmp.WriteFile($"P/X/f{i:00}.log", "1");
        var context = await ContextAsync([]);

        var differs = IgnoreRelocation.ProposeIntegrate(context, "P/X").Equivalence
            .Should().BeOfType<EquivalenceResult.Differs>().Subject;

        differs.Count.Should().Be(25);
        differs.Examples.Should().HaveCount(20);
    }

    [Fact]
    public void HashContent_ignores_a_BOM_and_line_ending_style()
    {
        IgnoreRelocation.HashContent("﻿a\r\nb\r\n").Should().Be(IgnoreRelocation.HashContent("a\nb\n"));
        IgnoreRelocation.HashContent("a\nb\n").Should().NotBe(IgnoreRelocation.HashContent("a\nc\n"));
        IgnoreRelocation.HashContent(null).Should().BeNull();
    }
}
