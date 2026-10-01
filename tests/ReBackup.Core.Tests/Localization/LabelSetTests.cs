using System.Text;
using FluentAssertions;
using ReBackup.Core.Localization;

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
}
