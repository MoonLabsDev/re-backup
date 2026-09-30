using System.Text.Json;
using ReBackup.Core.IO;
using ReBackup.Core.Json;

namespace ReBackup.Core.Plans;

public sealed record PlanLoadError(string FilePath, string Message);

public sealed record PlanLoadResult(IReadOnlyList<BackupPlan> Plans, IReadOnlyList<PlanLoadError> Errors);

public sealed class PlanStore : IDisposable
{
    public PlanStore(string plansDirectory)
    {
        PlansDirectory = Path.GetFullPath(plansDirectory);
        Directory.CreateDirectory(PlansDirectory);
    }

    public string PlansDirectory { get; }

    public string PathFor(string planId) => Path.Combine(PlansDirectory, planId + ".json");

    public PlanLoadResult LoadAll()
    {
        var plans = new List<BackupPlan>();
        var errors = new List<PlanLoadError>();

        var files = Directory.EnumerateFiles(PlansDirectory)
            .Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            try
            {
                var plan = JsonSerializer.Deserialize<BackupPlan>(File.ReadAllText(file), JsonDefaults.Options)
                    ?? throw new JsonException("File is empty.");
                var expectedId = Path.GetFileNameWithoutExtension(file);
                if (!string.Equals(plan.Id, expectedId, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException($"Plan id \"{plan.Id}\" does not match file name \"{expectedId}\".");
                plans.Add(plan);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                errors.Add(new PlanLoadError(file, ex.Message));
            }
        }

        return new PlanLoadResult(
            plans.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            errors);
    }

    public void Save(BackupPlan plan)
    {
        var path = PathFor(plan.Id);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(plan, JsonDefaults.Options));
    }

    public void Delete(string planId)
    {
        File.Delete(PathFor(planId));
    }

    public void Dispose()
    {
    }
}
