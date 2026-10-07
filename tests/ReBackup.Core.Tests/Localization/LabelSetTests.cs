using System.Text;
using FluentAssertions;
using ReBackup.Shared.Localization;

namespace ReBackup.Core.Tests.Localization;

public class LabelSetTests
{
    [Fact]
    public void Nested_objects_become_dotted_keys()
    {
        var set = LabelSet.Parse("""{ "ui": { "ignore": { "title": "Ignore" }, "x": "X" }, "raw": "{text}" }""");

        set.Entries.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ui.ignore.title"] = "Ignore",
            ["ui.x"] = "X",
            ["raw"] = "{text}",
        });
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var set = LabelSet.Parse("{\n  // a comment\n  \"a\": \"x\",\n}");

        set.TryGet("a", out var value).Should().BeTrue();
        value.Should().Be("x");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{ \"a\": 1 }")]
    [InlineData("{ \"a\": null }")]
    [InlineData("{ \"a\": [\"x\"] }")]
    [InlineData("{ \"a.b\": \"x\" }")]
    [InlineData("{ \"\": \"x\" }")]
    [InlineData("{ \"a\": \"x\", \"a\": \"y\" }")]
    [InlineData("{ not json")]
    public void Invalid_files_are_rejected(string json)
    {
        FluentActions.Invoking(() => LabelSet.Parse(json)).Should().Throw<FormatException>();
    }

    [Fact]
    public void A_stream_with_a_byte_order_mark_is_read()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{ \"a\": \"Größe\" }")).ToArray();

        LabelSet.Parse(new MemoryStream(bytes)).Entries["a"].Should().Be("Größe");
    }

    [Fact]
    public void Only_leaves_are_labels()
    {
        var set = LabelSet.Parse("""{ "a": { "b": "x" } }""");

        set.TryGet("a", out _).Should().BeFalse();
        set.TryGet("a.b", out _).Should().BeTrue();
    }

    [Fact]
    public void An_object_with_one_and_other_is_a_plural_label()
    {
        var set = LabelSet.Parse("""{ "a": { "files": { "one": "{count} file", "other": "{count:N0} files" } } }""");

        set.Plurals.Should().BeEquivalentTo(["a.files"]);
        set.IsPlural("a.files").Should().BeTrue();
        set.Entries.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["a.files.one"] = "{count} file",
            ["a.files.other"] = "{count:N0} files",
        });
        set.TryGet("a.files", out var text).Should().BeTrue();
        text.Should().Be("{count:N0} files", "the plain text of a plural label is its other form");
        set.TryGet("a.files", 1, out text).Should().BeTrue();
        text.Should().Be("{count} file");
        set.TryGet("a.files", 2, out text).Should().BeTrue();
        text.Should().Be("{count:N0} files");
    }

    [Fact]
    public void An_object_with_other_members_is_no_plural()
    {
        var set = LabelSet.Parse("""{ "a": { "one": "x", "other": "y", "few": "z" }, "b": { "one": "x" } }""");

        set.Plurals.Should().BeEmpty();
        set.IsPlural("a").Should().BeFalse();
        set.TryGet("a.one", out _).Should().BeTrue();
        set.TryGet("b.one", out _).Should().BeTrue();
    }

    [Fact]
    public void A_plain_label_ignores_the_count()
    {
        var set = LabelSet.Parse("""{ "a": "{count} x" }""");

        set.TryGet("a", 1, out var text).Should().BeTrue();
        text.Should().Be("{count} x");
        set.TryGet("missing", 1, out _).Should().BeFalse();
    }
}
