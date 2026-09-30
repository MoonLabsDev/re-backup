using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;

namespace ReBackup.Core.Tests.Indexing;

public class IndexEvaluatorTests
{
    private static IndexNode File(string path, long size) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..], RelativePath = path, IsDirectory = false, Size = size,
    };

    private static IndexNode Dir(string path, params IndexNode[] children) => new()
    {
        Name = path.Length == 0 ? "root" : path[(path.LastIndexOf('/') + 1)..],
        RelativePath = path, IsDirectory = true, Children = children,
    };

    // root: a.txt(10) b.tmp(5) build{ out.bin(100) keep.txt(1) } src{ x.cs(20) y.tmp(2) }
    private static SourceIndex SampleIndex() => new(
        @"C:\src",
        Dir("",
            File("a.txt", 10),
            File("b.tmp", 5),
            Dir("build", File("build/out.bin", 100), File("build/keep.txt", 1)),
            Dir("src", File("src/x.cs", 20), File("src/y.tmp", 2))),
        [], 6, 2);

    private static EvaluatedNode Child(EvaluatedNode node, string name) =>
        node.Children.Single(c => c.Node.Name == name);

    [Fact]
    public void Everything_is_included_without_patterns()
    {
        var root = IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], [], []));

        root.Status.Should().Be(IncludeStatus.Included);
        root.IncludedSize.Should().Be(138);
        root.IncludedFiles.Should().Be(6);
        root.IgnoredSize.Should().Be(0);
        root.IgnoredFiles.Should().Be(0);
        root.TotalSize.Should().Be(138);
        root.TotalFiles.Should().Be(6);
    }

    [Fact]
    public void Rolls_up_sizes_and_marks_partial_folders()
    {
        var matcher = IgnoreMatcher.Create([], ["*.tmp", "build/", "!build/keep.txt"], []);

        var root = IndexEvaluator.Evaluate(SampleIndex(), matcher);

        root.Status.Should().Be(IncludeStatus.Partial);
        root.IncludedSize.Should().Be(30);
        root.IncludedFiles.Should().Be(2);
        root.IgnoredSize.Should().Be(108);
        root.IgnoredFiles.Should().Be(4);

        var src = Child(root, "src");
        src.Status.Should().Be(IncludeStatus.Partial);
        Child(src, "x.cs").Status.Should().Be(IncludeStatus.Included);
        var yTmp = Child(src, "y.tmp");
        yTmp.Status.Should().Be(IncludeStatus.Ignored);
        yTmp.Pattern!.Text.Should().Be("*.tmp");
        yTmp.IgnoredByParent.Should().BeFalse();
    }

    [Fact]
    public void Entries_below_an_ignored_folder_are_ignored_by_parent()
    {
        var matcher = IgnoreMatcher.Create([], ["build/", "!build/keep.txt"], []);

        var build = Child(IndexEvaluator.Evaluate(SampleIndex(), matcher), "build");

        build.Status.Should().Be(IncludeStatus.Ignored);
        build.IgnoredByParent.Should().BeFalse();
        build.Pattern!.Text.Should().Be("build/");
        build.IgnoredSize.Should().Be(101);
        build.IgnoredFiles.Should().Be(2);

        var keep = Child(build, "keep.txt");
        keep.Status.Should().Be(IncludeStatus.Ignored);
        keep.IgnoredByParent.Should().BeTrue();
        keep.Pattern!.Text.Should().Be("build/");
    }

    [Fact]
    public void Reincluded_file_carries_the_negated_pattern()
    {
        var matcher = IgnoreMatcher.Create([], ["*.tmp", "!b.tmp"], []);

        var b = Child(IndexEvaluator.Evaluate(SampleIndex(), matcher), "b.tmp");

        b.Status.Should().Be(IncludeStatus.Included);
        b.Pattern!.Text.Should().Be("!b.tmp");
    }

    [Fact]
    public void Root_itself_is_never_ignored()
    {
        var root = IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], ["*"], []));

        root.Status.Should().Be(IncludeStatus.Partial);
        root.IncludedFiles.Should().Be(0);
        root.IgnoredFiles.Should().Be(6);
    }

    [Fact]
    public void Children_keep_the_index_order()
    {
        var root = IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], [], []));

        root.Children.Select(c => c.Node.Name).Should().Equal("a.txt", "b.tmp", "build", "src");
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], [], []), cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
