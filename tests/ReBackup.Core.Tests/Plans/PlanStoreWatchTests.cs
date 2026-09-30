using FluentAssertions;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Plans;

public class PlanStoreWatchTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task External_write_raises_ExternalChange()
    {
        using var store = new PlanStore(_tmp.PathOf("plans"));
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ExternalChange += (_, _) => raised.TrySetResult();
        store.StartWatching();

        File.WriteAllText(store.PathFor("abc"), """{ "id": "abc", "name": "X" }""");

        var completed = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(raised.Task);
    }

    [Fact]
    public async Task External_delete_raises_ExternalChange()
    {
        using var store = new PlanStore(_tmp.PathOf("plans"));
        var plan = new BackupPlan { Name = "X" };
        store.Save(plan);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ExternalChange += (_, _) => raised.TrySetResult();
        store.StartWatching();

        File.Delete(store.PathFor(plan.Id));

        var completed = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(raised.Task);
    }

    [Fact]
    public async Task Own_save_and_delete_do_not_raise_ExternalChange()
    {
        using var store = new PlanStore(_tmp.PathOf("plans"));
        var raised = false;
        store.ExternalChange += (_, _) => raised = true;
        store.StartWatching();

        var plan = new BackupPlan { Name = "Mine" };
        store.Save(plan);
        plan.Name = "Mine again";
        store.Save(plan);
        store.Delete(plan.Id);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        raised.Should().BeFalse();
    }

    [Fact]
    public async Task Repeated_own_save_does_not_raise_ExternalChange()
    {
        using var store = new PlanStore(_tmp.PathOf("plans"));
        var raised = false;
        store.ExternalChange += (_, _) => raised = true;
        store.StartWatching();

        var plan = new BackupPlan { Name = "Mine" };
        store.Save(plan);
        plan.Name = "Mine again";
        store.Save(plan);
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        raised.Should().BeFalse();
    }

    [Fact]
    public async Task External_rename_away_from_json_raises_ExternalChange()
    {
        using var store = new PlanStore(_tmp.PathOf("plans"));
        var plan = new BackupPlan { Name = "X" };
        store.Save(plan);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ExternalChange += (_, _) => raised.TrySetResult();
        store.StartWatching();

        File.Move(store.PathFor(plan.Id), store.PathFor(plan.Id) + ".bak");

        var completed = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().BeSameAs(raised.Task);
    }

    [Fact]
    public async Task Dispose_with_pending_events_does_not_throw_or_raise()
    {
        var store = new PlanStore(_tmp.PathOf("plans"));
        var raised = false;
        store.ExternalChange += (_, _) => raised = true;
        store.StartWatching();

        File.WriteAllText(store.PathFor("abc"), """{ "id": "abc", "name": "X" }""");
        await Task.Delay(100);
        var act = () => store.Dispose();
        act.Should().NotThrow();
        store.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        raised.Should().BeFalse();
    }
}
