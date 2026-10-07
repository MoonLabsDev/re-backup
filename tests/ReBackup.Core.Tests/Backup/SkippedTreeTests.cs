using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Localization;

namespace ReBackup.Core.Tests.Backup;

public class SkippedTreeTests
{
    private static readonly string Gone = CoreTexts.English("core.file.noLongerExists");
    private static readonly string Changed = CoreTexts.English("core.skip.changed");
    private static readonly string Locked = CoreTexts.English("core.file.locked");

    [Fact]
    public void Classifies_the_reasons()
    {
        SkippedTree.KindOf(Gone).Should().Be(SkipKind.Deleted);
        SkippedTree.KindOf(Changed).Should().Be(SkipKind.Changed);
        SkippedTree.KindOf(Locked).Should().Be(SkipKind.Other);
        SkippedTree.KindOf("Das System kann auf die Datei nicht zugreifen.").Should().Be(SkipKind.Other);
    }

    [Fact]
    public void Builds_folders_first_sorted_by_name_and_compacts_single_folder_chains()
    {
        var roots = SkippedTree.Build([
            new SkippedEntry("Apps/lib/data/mongo/WiredTiger.wt", Changed),
            new SkippedEntry("Apps/lib/data/mongo/.mongodb/mongosh/b_log", Gone),
            new SkippedEntry("Apps/lib/data/mongo/.mongodb/mongosh/A_log", Gone),
            new SkippedEntry("Apps/lib/data/mongo/diagnostic.data/metrics.interim", Changed),
            new SkippedEntry("top.txt", Locked),
        ]);

        roots.Select(n => n.Name).Should().Equal("Apps/lib/data/mongo", "top.txt");
        var mongo = roots[0];
        mongo.IsDirectory.Should().BeTrue();
        mongo.Path.Should().Be("Apps/lib/data/mongo");
        mongo.Children.Select(n => n.Name).Should().Equal(".mongodb/mongosh", "diagnostic.data", "WiredTiger.wt");
        mongo.Children[0].Children.Select(n => n.Name).Should().Equal(["A_log", "b_log"], "names without regard to case");
        (mongo.Deleted, mongo.Changed, mongo.Other).Should().Be((2, 2, 0));

        var file = mongo.Children[2];
        file.IsDirectory.Should().BeFalse();
        file.Path.Should().Be("Apps/lib/data/mongo/WiredTiger.wt");
        file.Kind.Should().Be(SkipKind.Changed);
        file.Reason.Should().Be(Changed);

        roots[1].Kind.Should().Be(SkipKind.Other);
        (roots[1].Deleted, roots[1].Changed, roots[1].Other).Should().Be((0, 0, 1));
    }

    [Fact]
    public void A_skipped_folder_keeps_its_reason_and_is_not_compacted_away()
    {
        var link = CoreTexts.English("core.scan.link");
        var roots = SkippedTree.Build([
            new SkippedEntry("a/linked", link),
            new SkippedEntry("a/b/unreadable", "access denied"),
            new SkippedEntry("a/b", "Folder cannot be read."),
        ]);

        var a = roots.Single();
        a.Name.Should().Be("a");
        a.Children.Select(n => n.Name).Should().Equal("b", "linked");
        a.Children[0].IsDirectory.Should().BeTrue();
        a.Children[0].Reason.Should().Be("Folder cannot be read.");
        a.Children[1].IsDirectory.Should().BeTrue("a link that is not followed is a folder");
        (a.Deleted, a.Changed, a.Other).Should().Be((0, 0, 3));
    }

    [Fact]
    public void Backslashes_and_duplicate_paths_are_handled()
    {
        var roots = SkippedTree.Build([
            new SkippedEntry(@"x\y.txt", Gone),
            new SkippedEntry("x/y.txt", Changed),
        ]);

        var y = roots.Single().Children.Single();
        y.Name.Should().Be("y.txt");
        y.Kind.Should().Be(SkipKind.Deleted, "the first entry wins");
    }
}
