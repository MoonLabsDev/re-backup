using FluentAssertions;
using ReBackup.Shared.Localization;

namespace ReBackup.Core.Tests.Localization;

public class MessageTests
{
    [Fact]
    public void Messages_with_the_same_key_and_arguments_are_equal()
    {
        Message.Of("a.b", ("n", 2), ("x", "y")).Should().Be(Message.Of("a.b", ("x", "y"), ("n", 2)));
        Message.Of("a.b", ("inner", Message.Of("c"))).Should().Be(Message.Of("a.b", ("inner", Message.Of("c"))));
        Message.Of("a.b", ("n", 2)).Should().NotBe(Message.Of("a.b", ("n", 3)));
        Message.Of("a.b", ("n", 2)).Should().NotBe(Message.Of("a.b"));
        Message.Of("a.b").Should().NotBe(Message.Of("a.c"));
    }

    [Fact]
    public void Raw_wraps_a_text_as_it_is()
    {
        var message = Message.Raw("Access to the path is denied.");

        message.Key.Should().Be(Message.RawKey);
        message.Args["text"].Should().Be("Access to the path is denied.");
    }

    [Fact]
    public void ToString_shows_the_key_and_the_sorted_arguments()
    {
        Message.Of("k", ("b", 1), ("a", "x")).ToString().Should().Be("k(a=x, b=1)");
        Message.Of("k").ToString().Should().Be("k");
    }

    [Fact]
    public void A_key_is_required()
    {
        FluentActions.Invoking(() => new Message(" ")).Should().Throw<ArgumentException>();
    }
}
