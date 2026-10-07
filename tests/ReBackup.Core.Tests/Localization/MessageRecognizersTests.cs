using FluentAssertions;
using ReBackup.Core.Localization;
using ReBackup.Shared.Localization;

namespace ReBackup.Core.Tests.Localization;

/// <summary>Recognizing a stored English text through every registered recognizer, nested arguments included.</summary>
public class MessageRecognizersTests
{
    private static readonly IReadOnlyList<Func<string, Message?>> Chain = [CoreTexts.Recognize, SharedTexts.Recognize];

    [Fact]
    public void A_nested_shared_problem_inside_a_Core_message_is_recognized()
    {
        var message = MessageRecognizers.Recognize("Retention rule 2: keep must be a number from 1 to 100.", Chain);

        message.Should().Be(Message.Of("core.plan.retentionRule", ("index", "2"),
            ("problem", Message.Of("shared.retention.keep", ("max", "100")))));
    }

    [Fact]
    public void A_nested_shared_trigger_problem_is_recognized()
    {
        var message = MessageRecognizers.Recognize("Trigger 1: the type is unknown.", Chain)!;

        message.Args["problem"].Should().Be(Message.Of("shared.trigger.unknownType"));
    }

    [Fact]
    public void Core_alone_leaves_the_shared_problem_as_text()
    {
        CoreTexts.Recognize("Retention rule 2: keep must be a number from 1 to 100.")!.Args["problem"]
            .Should().Be("keep must be a number from 1 to 100.");
    }

    [Fact]
    public void The_first_recognizer_that_matches_wins()
    {
        var first = (string text) => text == "x" ? Message.Of("first") : null;
        var second = (string text) => text == "x" ? Message.Of("second") : null;

        MessageRecognizers.Recognize("x", [first, second]).Should().Be(Message.Of("first"));
    }

    [Fact]
    public void A_later_recognizer_is_tried_when_the_earlier_ones_do_not_match()
    {
        MessageRecognizers.Recognize("the rule is empty.", Chain).Should().Be(Message.Of("shared.retention.empty"));
    }

    [Theory]
    [InlineData("device not ready")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_texts_are_not_recognized(string? text)
    {
        MessageRecognizers.Recognize(text, Chain).Should().BeNull();
    }

    [Fact]
    public void No_recognizers_recognize_nothing() =>
        MessageRecognizers.Recognize("the rule is empty.", []).Should().BeNull();
}
