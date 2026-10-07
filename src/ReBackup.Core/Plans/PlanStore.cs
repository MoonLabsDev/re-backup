using System.Collections.Concurrent;
using System.Text.Json;
using ReBackup.Core.Localization;
using ReBackup.Shared.IO;
using ReBackup.Shared.Json;

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
    private bool _disposed;

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
                plans.Add(ReadPlan(file));
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

    /// <summary>The plan as it is saved right now; null when there is no file for it.</summary>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="JsonException">The file is not a plan, or not the plan with this id.</exception>
    public BackupPlan? TryLoad(string planId)
    {
        try
        {
            return ReadPlan(PathFor(planId));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static BackupPlan ReadPlan(string file)
    {
        var plan = JsonSerializer.Deserialize<BackupPlan>(File.ReadAllText(file), JsonDefaults.Options)
            ?? throw new JsonException(CoreTexts.English("core.planFile.empty"));
        var expectedId = Path.GetFileNameWithoutExtension(file);
        if (!string.Equals(plan.Id, expectedId, StringComparison.OrdinalIgnoreCase))
            throw new JsonException(CoreTexts.English("core.planFile.idMismatch", ("id", plan.Id), ("file", expectedId)));
        // An entry written as null would reach the editor and the scheduler as a null reference.
        if (plan.Triggers.Any(trigger => trigger is null))
            throw new JsonException(CoreTexts.English("core.planFile.emptyTrigger"));
        if (plan.Retention.Any(rule => rule is null))
            throw new JsonException(CoreTexts.English("core.planFile.emptyRule"));
        plan.Ignore ??= new IgnoreSettings();
        plan.Ignore.Patterns ??= [];
        return plan;
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
        lock (_timerLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
    }

    private static bool IsJson(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private void OnFileEvent(object? sender, FileSystemEventArgs e)
    {
        var relevant = IsJson(e.FullPath);
        var oldRelevant = e is RenamedEventArgs r && IsJson(r.OldFullPath);
        if (!relevant && !oldRelevant)
            return;

        lock (_timerLock)
        {
            if (_disposed)
                return;

            if (relevant)
                _pending[e.FullPath] = 0;
            if (oldRelevant)
                _pending[((RenamedEventArgs)e).OldFullPath] = 0;

            _debounce ??= new Timer(_ => FlushPending());
            _debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    // Evaluated after the debounce so that Save() has recorded its stamp even when the
    // watcher event arrived before Save() returned.
    private void FlushPending()
    {
        var removed = new List<string>();
        lock (_timerLock)
        {
            if (_disposed)
                return;
            foreach (var path in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(path, out _))
                    removed.Add(path);
            }
        }

        if (!removed.Any(p => !IsOwnWrite(p)))
            return;

        lock (_timerLock)
        {
            if (_disposed)
                return;
        }

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
        FileSystemWatcher? watcher;
        Timer? timer;
        lock (_timerLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            watcher = _watcher;
            timer = _debounce;
            _watcher = null;
            _debounce = null;
        }

        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileEvent;
            watcher.Changed -= OnFileEvent;
            watcher.Deleted -= OnFileEvent;
            watcher.Renamed -= OnFileEvent;
            watcher.Dispose();
        }

        timer?.Dispose();
    }
}
