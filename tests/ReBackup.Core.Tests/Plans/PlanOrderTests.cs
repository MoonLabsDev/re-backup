using FluentAssertions;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Tests.Plans;

public class PlanOrderTests
{
    private static BackupPlan Plan(string id, string name) => new() { Id = id, Name = name };

    private static readonly BackupPlan Alpha = Plan("a", "Alpha");
    private static readonly BackupPlan Beta = Plan("b", "Beta");
    private static readonly BackupPlan Gamma = Plan("c", "Gamma");

    [Fact]
    public void Without_an_order_the_plans_are_sorted_by_name()
    {
        PlanOrder.Apply([Gamma, Alpha, Beta], []).Should().Equal(Alpha, Beta, Gamma);
    }

    [Fact]
    public void The_saved_order_decides()
    {
        PlanOrder.Apply([Alpha, Beta, Gamma], ["c", "a", "b"]).Should().Equal(Gamma, Alpha, Beta);
    }

    [Fact]
    public void Plans_the_order_does_not_name_come_last_sorted_by_name()
    {
        var delta = Plan("d", "Delta");

        PlanOrder.Apply([delta, Alpha, Beta, Gamma], ["b"]).Should().Equal(Beta, Alpha, delta, Gamma);
    }

    [Fact]
    public void Ids_of_deleted_plans_are_ignored()
    {
        PlanOrder.Apply([Alpha, Beta], ["gone", "b", "also-gone", "a"]).Should().Equal(Beta, Alpha);
    }

    [Fact]
    public void Ids_match_case_insensitively_and_the_first_mention_wins()
    {
        PlanOrder.Apply([Alpha, Beta], ["B", "a", "b"]).Should().Equal(Beta, Alpha);
    }

    [Fact]
    public void A_null_order_sorts_by_name()
    {
        PlanOrder.Apply([Beta, Alpha], null).Should().Equal(Alpha, Beta);
    }

    [Theory]
    [InlineData("a", 0)]        // before everything the order puts after it
    [InlineData("b", 1)]
    [InlineData("c", 2)]
    [InlineData("unknown", 3)]  // not in the order: at the end
    public void A_plan_that_appears_goes_where_the_order_puts_it(string id, int expected)
    {
        // The list holds x, y and an unsaved plan the order does not name.
        var current = new[] { "x", "y", "new" };

        PlanOrder.InsertionIndex(current, s => s, id, ["a", "x", "b", "y", "c"]).Should().Be(expected);
    }

    [Fact]
    public void Without_an_order_a_plan_that_appears_goes_to_the_end()
    {
        PlanOrder.InsertionIndex(new[] { "x", "y" }, s => s, "a", null).Should().Be(2);
    }

    [Fact]
    public void Works_for_any_item_with_an_id_and_a_name()
    {
        var items = new[] { ("x", "Zed"), ("y", "Abe"), ("z", "Max") };

        PlanOrder.Apply(items, i => i.Item1, i => i.Item2, ["z"])
            .Select(i => i.Item1).Should().Equal("z", "y", "x");
    }
}
