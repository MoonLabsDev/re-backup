using ReBackup.Core.IO;
using ReBackup.Core.Localization;
using ReBackup.Core.Retention;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Plans;

public static class PlanValidator
{
    /// <summary>What is wrong with the plan (keys under <c>core.plan</c>); empty when it can be saved.</summary>
    public static IReadOnlyList<Message> Validate(BackupPlan plan, IEnumerable<BackupPlan> allPlans)
    {
        var errors = new List<Message>();
        ValidateName(plan, allPlans, errors);

        var sourceOk = ValidatePath(plan.Source, "core.plan.sourceRequired", "core.plan.sourceNotAbsolute", errors);
        if (sourceOk && !Directory.Exists(plan.Source))
            errors.Add(Message.Of("core.plan.sourceMissing"));
        var targetOk = ValidatePath(plan.Target, "core.plan.targetRequired", "core.plan.targetNotAbsolute", errors);

        if (sourceOk && targetOk)
        {
            if (PathUtil.IsSameOrInside(plan.Target, plan.Source))
                errors.Add(Message.Of("core.plan.targetInsideSource"));
            else if (PathUtil.IsSameOrInside(plan.Source, plan.Target))
                errors.Add(Message.Of("core.plan.sourceInsideTarget"));
        }

        for (var i = 0; i < plan.Retention.Count; i++)
        {
            if (RetentionRules.Validate(plan.Retention[i]) is { } problem)
                errors.Add(Message.Of("core.plan.retentionRule", ("index", i + 1), ("problem", problem)));
        }

        for (var i = 0; i < plan.Triggers.Count; i++)
        {
            if (ScheduleTriggers.Validate(plan.Triggers[i]) is { } problem)
                errors.Add(Message.Of("core.plan.trigger", ("index", i + 1), ("problem", problem)));
        }

        return errors;
    }

    private static void ValidateName(BackupPlan plan, IEnumerable<BackupPlan> allPlans, List<Message> errors)
    {
        var problems = NameErrors(plan.Name);
        errors.AddRange(problems);
        if (problems.Count == 0 &&
            allPlans.Any(p => p.Id != plan.Id && string.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add(Message.Of("core.plan.nameTaken", ("name", plan.Name)));
    }

    /// <summary>What is wrong with a plan name on its own (uniqueness is not checked). Empty when it can be used.</summary>
    public static IReadOnlyList<Message> NameErrors(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return [Message.Of("core.plan.nameRequired")];

        var errors = new List<Message>();
        if (name != name.Trim())
            errors.Add(Message.Of("core.plan.nameSpaces"));
        if (name.EndsWith('.'))
            errors.Add(Message.Of("core.plan.nameDot"));
        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            errors.Add(Message.Of("core.plan.namePartial"));
        if (name.EndsWith(".deleting", StringComparison.OrdinalIgnoreCase))
            errors.Add(Message.Of("core.plan.nameDeleting"));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add(Message.Of("core.plan.nameChars"));
        return errors;
    }

    private static bool ValidatePath(string path, string requiredKey, string absoluteKey, List<Message> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add(Message.Of(requiredKey));
            return false;
        }
        if (!Path.IsPathFullyQualified(path))
        {
            errors.Add(Message.Of(absoluteKey));
            return false;
        }
        return true;
    }
}
