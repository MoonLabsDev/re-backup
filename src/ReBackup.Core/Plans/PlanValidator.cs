using ReBackup.Core.IO;
using ReBackup.Core.Retention;
using ReBackup.Core.Schedule;

namespace ReBackup.Core.Plans;

public static class PlanValidator
{
    public static IReadOnlyList<string> Validate(BackupPlan plan, IEnumerable<BackupPlan> allPlans)
    {
        var errors = new List<string>();
        ValidateName(plan, allPlans, errors);

        var sourceOk = ValidatePath(plan.Source, "Source", errors);
        if (sourceOk && !Directory.Exists(plan.Source))
            errors.Add("Source folder does not exist.");
        var targetOk = ValidatePath(plan.Target, "Target", errors);

        if (sourceOk && targetOk)
        {
            if (PathUtil.IsSameOrInside(plan.Target, plan.Source))
                errors.Add("Target must not be inside the source.");
            else if (PathUtil.IsSameOrInside(plan.Source, plan.Target))
                errors.Add("Source must not be inside the target.");
        }

        for (var i = 0; i < plan.Retention.Count; i++)
        {
            if (RetentionRules.Validate(plan.Retention[i]) is { } problem)
                errors.Add($"Retention rule {i + 1}: {problem}");
        }

        for (var i = 0; i < plan.Triggers.Count; i++)
        {
            if (ScheduleTriggers.Validate(plan.Triggers[i]) is { } problem)
                errors.Add($"Trigger {i + 1}: {problem}");
        }

        return errors;
    }

    private static void ValidateName(BackupPlan plan, IEnumerable<BackupPlan> allPlans, List<string> errors)
    {
        var problems = NameErrors(plan.Name);
        errors.AddRange(problems);
        if (problems.Count == 0 &&
            allPlans.Any(p => p.Id != plan.Id && string.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Another plan is already named \"{plan.Name}\".");
    }

    /// <summary>What is wrong with a plan name on its own (uniqueness is not checked). Empty when it can be used.</summary>
    public static IReadOnlyList<string> NameErrors(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ["Name is required."];

        var errors = new List<string>();
        if (name != name.Trim())
            errors.Add("Name must not start or end with spaces.");
        if (name.EndsWith('.'))
            errors.Add("Name must not end with a dot.");
        if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            errors.Add("Name must not end with \".partial\".");
        if (name.EndsWith(".deleting", StringComparison.OrdinalIgnoreCase))
            errors.Add("Name must not end with \".deleting\".");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add("Name contains characters that are not allowed in folder names.");
        return errors;
    }

    private static bool ValidatePath(string path, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{label} folder is required.");
            return false;
        }
        if (!Path.IsPathFullyQualified(path))
        {
            errors.Add($"{label} must be an absolute path.");
            return false;
        }
        return true;
    }
}
