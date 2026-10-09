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

    [Fact]
    public void Works_for_any_item_with_an_id_and_a_name()
    {
        var items = new[] { ("x", "Zed"), ("y", "Abe"), ("z", "Max") };

        PlanOrder.Apply(items, i => i.Item1, i => i.Item2, ["z"])
            .Select(i => i.Item1).Should().Equal("z", "y", "x");
    }
}
