using FluentAssertions;
using ReBackup.Core.Indexing;
using ReBackup.Core.Plans;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;

namespace ReBackup.Core.Tests.Indexing;

/// <summary>Scanned entries keep their name and parent, not their path; entries of the same name share one string.</summary>
public class TreePathTests
{
    private static InMemoryStorage Source()
    {
        var source = new InMemoryStorage(StorageCapabilities.None);
        source.AddFile("a/lib/index.js", [1]);
        source.AddFile("b/lib/index.js", [2]);
        source.AddFile("top.txt", [3]);
        return source;
    }

    [Fact]
    public async Task Paths_are_put_together_from_the_names()
    {
        var index = await SourceIndexer.BuildAsync(Source(), rootName: "src");

        var (first, second) = (Find(index.RootNode, "a/lib/index.js"), Find(index.RootNode, "b/lib/index.js"));

        index.RootNode.RelativePath.Should().Be("", "the root's name is a display name, no part of a path");
        Find(index.RootNode, "top.txt").RelativePath.Should().Be("top.txt");
        first.Parent!.RelativePath.Should().Be("a/lib");
        ReferenceEquals(first.Name, second.Name).Should().BeTrue("names that repeat are kept once per scan");
    }

    [Fact]
    public async Task A_live_scan_gives_the_same_paths()
    {
        var scan = LiveScan.Start(Source(), "src", new IgnoreSettings(), []);
        var index = await scan.Completion;

        var file = Find(index.RootNode, "b/lib/index.js");
        file.Parent!.Parent!.Name.Should().Be("b");
        scan.Root.RelativePath.Should().Be("");
        ReferenceEquals(file.Name, Find(index.RootNode, "a/lib/index.js").Name).Should().BeTrue();
    }

    /// <summary>Walks down by name, so that the lookup does not rely on the paths it checks.</summary>
    private static IndexNode Find(IndexNode root, string path)
    {
        var node = root;
        foreach (var part in path.Split('/'))
            node = node.Children.Single(c => c.Name == part);
        node.RelativePath.Should().Be(path);
        return node;
    }
}
