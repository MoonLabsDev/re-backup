using System.Collections.Concurrent;
using System.Text.Json;
using ReBackup.Core.IO;
using ReBackup.Core.Json;

namespace ReBackup.Core.Plans;

public sealed record PlanLoadError(string FilePath, string Message);

public sealed record PlanLoadResult(IReadOnlyList<BackupPlan> Plans, IReadOnlyList<PlanLoadError> Errors);

public sealed class PlanStore : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    // Last-write stamp of files this store wrote itself; DateTime.MinValue marks an own delete.
    private readonly ConcurrentDictionary<string, DateTime> _ownWrites = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _timerLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public PlanStore(string plansDirectory)
    {
        PlansDirectory = Path.GetFullPath(plansDirectory);
        Directory.CreateDirectory(PlansDirectory);
    }

    public event EventHandler? ExternalChange;

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
        _ownWrites[path] = File.GetLastWriteTimeUtc(path);
    }

    public void Delete(string planId)
    {
        var path = PathFor(planId);
        _ownWrites[path] = DateTime.MinValue;
        File.Delete(path);
    }

    public void StartWatching()
    {
        if (_watcher is not null)
            return;

        _watcher = new FileSystemWatcher(PlansDirectory, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        _watcher.Created += OnFileEvent;
        _watcher.Changed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object? sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return;

        _pending[e.FullPath] = 0;
        lock (_timerLock)
        {
            _debounce ??= new Timer(_ => FlushPending());
            _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // Evaluated after the debounce so that Save() has recorded its stamp even when the
    // watcher event arrived before Save() returned.
    private void FlushPending()
    {
        var paths = _pending.Keys.ToList();
        foreach (var path in paths)
            _pending.TryRemove(path, out _);

        if (paths.Any(p => !IsOwnWrite(p)))
            ExternalChange?.Invoke(this, EventArgs.Empty);
    }

    private bool IsOwnWrite(string path)
    {
        if (!_ownWrites.TryGetValue(path, out var stamp))
            return false;
        if (!File.Exists(path))
            return stamp == DateTime.MinValue;
        try
        {
            return File.GetLastWriteTimeUtc(path) == stamp;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        lock (_timerLock)
            _debounce?.Dispose();
    }
}
