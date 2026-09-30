# ReBackup Phase 1 — Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A runnable WPF app that lists, creates, edits (General tab), validates, saves and deletes backup plans stored as one JSON file per plan, with a settings dialog, a tray icon and close-to-tray behaviour.

**Architecture:** `ReBackup.Core` (plain .NET 9 class library) holds the plan model, validation, JSON storage, settings and config-folder location. `ReBackup.App` (WPF, MVVM via CommunityToolkit.Mvvm) is a thin shell over Core. `ReBackup.Core.Tests` (xUnit + FluentAssertions) covers all of Core. Later phases add typed plan sections (triggers, ignore, retention). Until then, unknown JSON properties round-trip through `[JsonExtensionData]`.

**Tech Stack:** .NET 9 (SDK 9.0.318 installed), WPF, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, System.Text.Json, xUnit, FluentAssertions 7.0.0.

**Spec:** `docs/superpowers/specs/2026-09-30-rebackup-design.md` (this plan covers §3, §4.1, §4.2, §10.1 General tab, §10.3 tray Open/Exit, §10.4 and build phase 1 of §13)

## Global Constraints

- Target frameworks: `net9.0` for Core and Tests, `net9.0-windows` for App.
- Nullable reference types and implicit usings are enabled solution-wide.
- JSON: camelCase, indented, enums as strings, UTF-8 without BOM. All JSON options come from `JsonDefaults.Options`.
- Config folder default: `%AppData%\ReBackup\`. Layout: `settings.json`, `plans/<plan-id>.json`, `logs/<plan-id>.jsonl`. The pointer file is `%AppData%\ReBackup\location.json`.
- Plan file name is `<id>.json`, and `id` inside the file must equal the file name.
- Every file write to the config folder goes through `AtomicFile.WriteAllText` (temp file + rename).
- Default ignore patterns: `Thumbs.db`, `desktop.ini`, `$RECYCLE.BIN/`, `System Volume Information/`.
- Close-to-tray defaults to `true`, start-with-Windows to `false` (HKCU `Software\Microsoft\Windows\CurrentVersion\Run`, value `ReBackup`).
- FluentAssertions is pinned to **7.0.0** (v8+ is commercially licensed).
- Every commit message ends with the trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## File Structure

```
ReBackup.sln
Directory.Build.props
.gitignore
src/ReBackup.Core/
  ReBackup.Core.csproj
  IO/PathUtil.cs              path normalisation + containment
  IO/AtomicFile.cs            temp-file-then-rename writes
  Json/JsonDefaults.cs        shared JsonSerializerOptions
  Plans/BackupPlan.cs         plan model (+ extension data)
  Plans/PlanValidator.cs      validation rules
  Plans/PlanStore.cs          load/save/delete/watch plan files
  Config/ConfigPaths.cs       paths inside a config folder
  Config/ConfigLocation.cs    resolve/move the config folder
  Settings/AppSettings.cs     global settings model
  Settings/SettingsStore.cs   load/save settings.json
src/ReBackup.App/
  ReBackup.App.csproj
  App.xaml / App.xaml.cs      composition root, tray, exit flow
  MainWindow.xaml / .cs       plan list + General tab
  SettingsWindow.xaml / .cs   settings dialog
  Services/IDialogService.cs
  Services/WpfDialogService.cs
  Services/StartupRegistration.cs
  ViewModels/PlanEditorViewModel.cs
  ViewModels/MainViewModel.cs
  ViewModels/SettingsViewModel.cs
tests/ReBackup.Core.Tests/
  ReBackup.Core.Tests.csproj
  TestSupport/TempDir.cs
  IO/PathUtilTests.cs
  IO/AtomicFileTests.cs
  Plans/PlanValidatorTests.cs
  Plans/PlanStoreTests.cs
  Plans/PlanStoreWatchTests.cs
  Settings/SettingsStoreTests.cs
  Config/ConfigLocationTests.cs
```

---

### Task 1: Solution scaffold, PathUtil, AtomicFile

**Files:**
- Create: `ReBackup.sln`, `Directory.Build.props`, `.gitignore`, the three projects
- Create: `src/ReBackup.Core/IO/PathUtil.cs`, `src/ReBackup.Core/IO/AtomicFile.cs`
- Create: `tests/ReBackup.Core.Tests/TestSupport/TempDir.cs`
- Test: `tests/ReBackup.Core.Tests/IO/PathUtilTests.cs`, `tests/ReBackup.Core.Tests/IO/AtomicFileTests.cs`

**Interfaces:**
- Produces: `PathUtil.Normalize(string) → string`, `PathUtil.IsSameOrInside(string path, string root) → bool`, `AtomicFile.WriteAllText(string path, string contents)`, test helper `TempDir` (`Root`, `PathOf`, `CreateDir`, `WriteFile`).

- [ ] **Step 1: Scaffold the solution** (run from the repo root)

```bash
dotnet new sln -n ReBackup
dotnet new classlib -n ReBackup.Core -o src/ReBackup.Core -f net9.0
dotnet new wpf -n ReBackup.App -o src/ReBackup.App -f net9.0
dotnet new xunit -n ReBackup.Core.Tests -o tests/ReBackup.Core.Tests -f net9.0
dotnet sln add src/ReBackup.Core src/ReBackup.App tests/ReBackup.Core.Tests
dotnet add src/ReBackup.App reference src/ReBackup.Core
dotnet add tests/ReBackup.Core.Tests reference src/ReBackup.Core
dotnet add src/ReBackup.App package CommunityToolkit.Mvvm
dotnet add src/ReBackup.App package H.NotifyIcon.Wpf
dotnet add tests/ReBackup.Core.Tests package FluentAssertions --version 7.0.0
dotnet new gitignore
rm src/ReBackup.Core/Class1.cs tests/ReBackup.Core.Tests/UnitTest1.cs
```

- [ ] **Step 2: Create `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Create the test helper `tests/ReBackup.Core.Tests/TestSupport/TempDir.cs`**

```csharp
namespace ReBackup.Core.Tests.TestSupport;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rebackup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) => System.IO.Path.Combine(Root, relative);

    public string CreateDir(string relative)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteFile(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: Write the failing tests**

`tests/ReBackup.Core.Tests/IO/PathUtilTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.IO;

namespace ReBackup.Core.Tests.IO;

public class PathUtilTests
{
    [Theory]
    [InlineData(@"C:\data\backup", @"C:\data", true)]
    [InlineData(@"C:\data", @"C:\data", true)]
    [InlineData(@"C:\DATA\x", @"c:\data\", true)]
    [InlineData(@"C:\database", @"C:\data", false)]
    [InlineData(@"D:\data", @"C:\data", false)]
    [InlineData(@"C:\x", @"C:\", true)]
    [InlineData(@"C:\data\..\other", @"C:\data", false)]
    public void IsSameOrInside_compares_normalised_paths(string path, string root, bool expected)
    {
        PathUtil.IsSameOrInside(path, root).Should().Be(expected);
    }

    [Fact]
    public void Normalize_removes_trailing_separator_but_keeps_drive_root()
    {
        PathUtil.Normalize(@"C:\data\").Should().Be(@"C:\data");
        PathUtil.Normalize(@"C:\").Should().Be(@"C:\");
    }
}
```

`tests/ReBackup.Core.Tests/IO/AtomicFileTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.IO;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.IO;

public class AtomicFileTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Writes_new_file_and_creates_directory()
    {
        var path = _tmp.PathOf(@"a\b\file.json");

        AtomicFile.WriteAllText(path, "hello");

        File.ReadAllText(path).Should().Be("hello");
    }

    [Fact]
    public void Overwrites_existing_file_and_leaves_no_temp_file()
    {
        var path = _tmp.WriteFile("file.json", "old");

        AtomicFile.WriteAllText(path, "new");

        File.ReadAllText(path).Should().Be("new");
        File.Exists(path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Writes_utf8_without_bom()
    {
        var path = _tmp.PathOf("file.json");

        AtomicFile.WriteAllText(path, "ä");

        File.ReadAllBytes(path).Should().Equal(0xC3, 0xA4);
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: build error `The type or namespace name 'IO' does not exist in the namespace 'ReBackup.Core'`.

- [ ] **Step 6: Implement `src/ReBackup.Core/IO/PathUtil.cs`**

```csharp
namespace ReBackup.Core.IO;

public static class PathUtil
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsSameOrInside(string path, string root)
    {
        var p = WithTrailingSeparator(Normalize(path));
        var r = WithTrailingSeparator(Normalize(root));
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
}
```

- [ ] **Step 7: Implement `src/ReBackup.Core/IO/AtomicFile.cs`**

```csharp
using System.Text;

namespace ReBackup.Core.IO;

public static class AtomicFile
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAllText(string path, string contents)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, contents, Utf8NoBom);
        File.Move(tempPath, fullPath, overwrite: true);
    }
}
```

- [ ] **Step 8: Run the tests and the full build**

Run: `dotnet build` then `dotnet test tests/ReBackup.Core.Tests`
Expected: build succeeds with 0 errors; tests PASS (11 tests).

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "chore: scaffold ReBackup solution with PathUtil and AtomicFile" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Plan model, JSON defaults and validation

**Files:**
- Create: `src/ReBackup.Core/Json/JsonDefaults.cs`, `src/ReBackup.Core/Plans/BackupPlan.cs`, `src/ReBackup.Core/Plans/PlanValidator.cs`
- Test: `tests/ReBackup.Core.Tests/Plans/PlanValidatorTests.cs`

**Interfaces:**
- Consumes: `PathUtil.IsSameOrInside`
- Produces:
  - `JsonDefaults.Options : JsonSerializerOptions`
  - `BackupPlan` with `string Id` (default: new GUID "N" format), `string Name`, `string Source`, `string Target`, `bool Enabled` (default `true`), `bool FreeSpaceByRetention`, `Dictionary<string, JsonElement>? Extra` (`[JsonExtensionData]`), `BackupPlan Clone()`
  - `PlanValidator.Validate(BackupPlan plan, IEnumerable<BackupPlan> allPlans) → IReadOnlyList<string>` (plans with the same `Id` as `plan` are ignored for uniqueness)

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Plans/PlanValidatorTests.cs`

```csharp
using FluentAssertions;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Plans;

public class PlanValidatorTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private BackupPlan ValidPlan() => new()
    {
        Name = "Projects",
        Source = _tmp.CreateDir("src"),
        Target = _tmp.PathOf("dst"),
    };

    private static IReadOnlyList<string> Validate(BackupPlan plan, params BackupPlan[] others) =>
        PlanValidator.Validate(plan, others.Append(plan));

    [Fact]
    public void Valid_plan_has_no_errors()
    {
        Validate(ValidPlan()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "Name is required.")]
    [InlineData("   ", "Name is required.")]
    [InlineData(" Projects", "Name must not start or end with spaces.")]
    [InlineData("Projects.", "Name must not end with a dot.")]
    [InlineData("a/b", "Name contains characters that are not allowed in folder names.")]
    [InlineData("a:b", "Name contains characters that are not allowed in folder names.")]
    public void Invalid_names_are_reported(string name, string expected)
    {
        var plan = ValidPlan();
        plan.Name = name;

        Validate(plan).Should().Contain(expected);
    }

    [Fact]
    public void Duplicate_name_in_another_plan_is_reported_case_insensitively()
    {
        var plan = ValidPlan();
        var other = ValidPlan();
        other.Name = "PROJECTS";

        Validate(plan, other).Should().Contain("Another plan is already named \"Projects\".");
    }

    [Fact]
    public void Same_plan_is_not_a_duplicate_of_itself()
    {
        var plan = ValidPlan();
        var sameIdCopy = plan.Clone();

        PlanValidator.Validate(plan, [plan, sameIdCopy]).Should().BeEmpty();
    }

    [Fact]
    public void Missing_source_and_target_are_reported()
    {
        var plan = ValidPlan();
        plan.Source = "";
        plan.Target = "";

        Validate(plan).Should().Contain(["Source folder is required.", "Target folder is required."]);
    }

    [Fact]
    public void Relative_paths_are_reported()
    {
        var plan = ValidPlan();
        plan.Source = "relative";
        plan.Target = @"also\relative";

        Validate(plan).Should().Contain(["Source must be an absolute path.", "Target must be an absolute path."]);
    }

    [Fact]
    public void Nonexistent_source_is_reported()
    {
        var plan = ValidPlan();
        plan.Source = _tmp.PathOf("missing");

        Validate(plan).Should().Contain("Source folder does not exist.");
    }

    [Fact]
    public void Target_inside_source_is_reported()
    {
        var plan = ValidPlan();
        plan.Target = Path.Combine(plan.Source, "backups");

        Validate(plan).Should().Contain("Target must not be inside the source.");
    }

    [Fact]
    public void Source_inside_target_is_reported()
    {
        var plan = ValidPlan();
        plan.Target = _tmp.Root;

        Validate(plan).Should().Contain("Source must not be inside the target.");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~PlanValidatorTests"`
Expected: build error `The type or namespace name 'Plans' does not exist`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Json/JsonDefaults.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Core.Json;

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            AllowOutOfOrderMetadataProperties = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
```

- [ ] **Step 4: Implement `src/ReBackup.Core/Plans/BackupPlan.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using ReBackup.Core.Json;

namespace ReBackup.Core.Plans;

public sealed class BackupPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool FreeSpaceByRetention { get; set; }

    /// <summary>Plan sections not yet modelled by this version (triggers, ignore, retention) survive a load/save round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public BackupPlan Clone() =>
        JsonSerializer.Deserialize<BackupPlan>(JsonSerializer.Serialize(this, JsonDefaults.Options), JsonDefaults.Options)!;
}
```

- [ ] **Step 5: Implement `src/ReBackup.Core/Plans/PlanValidator.cs`**

```csharp
using ReBackup.Core.IO;

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

        return errors;
    }

    private static void ValidateName(BackupPlan plan, IEnumerable<BackupPlan> allPlans, List<string> errors)
    {
        var name = plan.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Name is required.");
            return;
        }
        if (name != name.Trim())
            errors.Add("Name must not start or end with spaces.");
        if (name.EndsWith('.'))
            errors.Add("Name must not end with a dot.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            errors.Add("Name contains characters that are not allowed in folder names.");
        if (allPlans.Any(p => p.Id != plan.Id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            errors.Add($"Another plan is already named \"{name}\".");
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
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~PlanValidatorTests"`
Expected: PASS (14 tests).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): add BackupPlan model and PlanValidator" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: PlanStore load / save / delete

**Files:**
- Create: `src/ReBackup.Core/Plans/PlanStore.cs`
- Test: `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs`

**Interfaces:**
- Consumes: `BackupPlan`, `JsonDefaults.Options`, `AtomicFile.WriteAllText`
- Produces:
  - `record PlanLoadError(string FilePath, string Message)`
  - `record PlanLoadResult(IReadOnlyList<BackupPlan> Plans, IReadOnlyList<PlanLoadError> Errors)`, where plans are sorted by name, case-insensitive
  - `PlanStore(string plansDirectory)` (creates the directory), `string PlansDirectory`, `string PathFor(string planId)`, `PlanLoadResult LoadAll()`, `void Save(BackupPlan plan)`, `void Delete(string planId)`, `IDisposable`
  - Watching (`StartWatching`, `ExternalChange`) is added in Task 4

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs`

```csharp
using FluentAssertions;
using ReBackup.Core.Plans;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Plans;

public class PlanStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private PlanStore NewStore() => new(_tmp.PathOf("plans"));

    [Fact]
    public void Constructor_creates_the_directory()
    {
        using var store = NewStore();

        Directory.Exists(store.PlansDirectory).Should().BeTrue();
    }

    [Fact]
    public void Save_then_LoadAll_round_trips_all_fields()
    {
        using var store = NewStore();
        var plan = new BackupPlan
        {
            Name = "Projects", Source = @"D:\Projects", Target = @"F:\Backups",
            Enabled = false, FreeSpaceByRetention = true,
        };

        store.Save(plan);
        var loaded = store.LoadAll();

        loaded.Errors.Should().BeEmpty();
        loaded.Plans.Should().ContainSingle().Which.Should().BeEquivalentTo(plan);
    }

    [Fact]
    public void Save_writes_id_named_camelCase_file_without_temp_leftovers()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };

        store.Save(plan);

        var path = Path.Combine(store.PlansDirectory, plan.Id + ".json");
        store.PathFor(plan.Id).Should().Be(path);
        File.ReadAllText(path).Should().Contain("\"name\": \"Projects\"");
        Directory.GetFiles(store.PlansDirectory).Should().ContainSingle();
    }

    [Fact]
    public void Unknown_properties_survive_load_and_save()
    {
        using var store = NewStore();
        var path = store.PathFor("p1");
        File.WriteAllText(path, """
            { "id": "p1", "name": "Keep", "source": "C:\\x", "target": "D:\\y",
              "triggers": [ { "type": "Daily", "time": "02:00" } ] }
            """);

        var plan = store.LoadAll().Plans.Single();
        store.Save(plan);

        File.ReadAllText(path).Should().Contain("\"triggers\"").And.Contain("02:00");
    }

    [Fact]
    public void Unreadable_file_is_reported_and_other_plans_still_load()
    {
        using var store = NewStore();
        store.Save(new BackupPlan { Name = "Good" });
        File.WriteAllText(Path.Combine(store.PlansDirectory, "bad.json"), "{ not json");

        var result = store.LoadAll();

        result.Plans.Should().ContainSingle().Which.Name.Should().Be("Good");
        result.Errors.Should().ContainSingle().Which.FilePath.Should().EndWith("bad.json");
    }

    [Fact]
    public void Id_not_matching_file_name_is_reported()
    {
        using var store = NewStore();
        File.WriteAllText(Path.Combine(store.PlansDirectory, "other.json"), """{ "id": "p1", "name": "X" }""");

        var result = store.LoadAll();

        result.Plans.Should().BeEmpty();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("does not match");
    }

    [Fact]
    public void Non_json_files_are_ignored()
    {
        using var store = NewStore();
        File.WriteAllText(Path.Combine(store.PlansDirectory, "p1.json.tmp"), "garbage");
        File.WriteAllText(Path.Combine(store.PlansDirectory, "readme.txt"), "garbage");

        var result = store.LoadAll();

        result.Plans.Should().BeEmpty();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_sorts_by_name_case_insensitively()
    {
        using var store = NewStore();
        store.Save(new BackupPlan { Name = "beta" });
        store.Save(new BackupPlan { Name = "Alpha" });
        store.Save(new BackupPlan { Name = "gamma" });

        store.LoadAll().Plans.Select(p => p.Name).Should().Equal("Alpha", "beta", "gamma");
    }

    [Fact]
    public void Delete_removes_the_file_and_ignores_missing_files()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Gone" };
        store.Save(plan);

        store.Delete(plan.Id);
        store.Delete("does-not-exist");

        store.LoadAll().Plans.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~PlanStoreTests"`
Expected: build error `The type or namespace name 'PlanStore' could not be found`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Plans/PlanStore.cs`**

```csharp
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~PlanStoreTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add PlanStore with load, save and delete" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: PlanStore external-change watching

**Files:**
- Modify: `src/ReBackup.Core/Plans/PlanStore.cs`
- Test: `tests/ReBackup.Core.Tests/Plans/PlanStoreWatchTests.cs`

**Interfaces:**
- Consumes: `PlanStore` from Task 3
- Produces: `PlanStore.StartWatching()`, `event EventHandler? PlanStore.ExternalChange`. This is raised on a thread-pool thread about 500 ms after the last change to a `*.json` file in the plans folder that was **not** made by this store's own `Save`/`Delete`. Callers must marshal it to the UI thread.

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Plans/PlanStoreWatchTests.cs`

```csharp
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
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~PlanStoreWatchTests"`
Expected: build error `'PlanStore' does not contain a definition for 'ExternalChange'`.

- [ ] **Step 3: Replace `src/ReBackup.Core/Plans/PlanStore.cs` with the watching version**

The whole file is shown. `LoadAll` is unchanged from Task 3. `Save`, `Delete` and `Dispose` change, and watching is new.

```csharp
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
```

- [ ] **Step 4: Run all Core tests**

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: PASS (all tests, including the 3 watch tests and the 9 PlanStore tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): detect external changes to plan files" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Settings and config-folder location

**Files:**
- Create: `src/ReBackup.Core/Config/ConfigPaths.cs`, `src/ReBackup.Core/Config/ConfigLocation.cs`, `src/ReBackup.Core/Settings/AppSettings.cs`, `src/ReBackup.Core/Settings/SettingsStore.cs`
- Test: `tests/ReBackup.Core.Tests/Settings/SettingsStoreTests.cs`, `tests/ReBackup.Core.Tests/Config/ConfigLocationTests.cs`

**Interfaces:**
- Consumes: `AtomicFile`, `PathUtil`, `JsonDefaults`
- Produces:
  - `record ConfigPaths(string Root)` with `PlansDirectory`, `LogsDirectory`, `SettingsFile`, `LogFileFor(string planId)`
  - `enum ConfigMoveMode { CopyCurrent, UseExisting }`
  - `ConfigLocation.DefaultAppDataRoot`, `ConfigLocation.PointerFileName` (= `"location.json"`), `ConfigLocation.Resolve(string appDataRoot) → ConfigPaths`, `ConfigLocation.ContainsConfiguration(string root) → bool`, `ConfigLocation.Move(string appDataRoot, ConfigPaths current, string newRoot, ConfigMoveMode mode) → ConfigPaths`
  - `AppSettings` with `BuiltInIgnoreDefaults` (static), `List<string> DefaultIgnorePatterns`, `bool CloseToTray` (default true), `bool StartWithWindows` (default false)
  - `SettingsStore(string settingsFile)`, `AppSettings Load()`, `void Save(AppSettings)`, `string? LastLoadError`

- [ ] **Step 1: Write the failing tests**

`tests/ReBackup.Core.Tests/Settings/SettingsStoreTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Settings;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Missing_file_yields_defaults()
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));

        var settings = store.Load();

        settings.CloseToTray.Should().BeTrue();
        settings.StartWithWindows.Should().BeFalse();
        settings.DefaultIgnorePatterns.Should().Equal("Thumbs.db", "desktop.ini", "$RECYCLE.BIN/", "System Volume Information/");
        store.LastLoadError.Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var store = new SettingsStore(_tmp.PathOf("settings.json"));
        var settings = new AppSettings
        {
            CloseToTray = false,
            StartWithWindows = true,
            DefaultIgnorePatterns = ["*.bak"],
        };

        store.Save(settings);

        store.Load().Should().BeEquivalentTo(settings);
    }

    [Fact]
    public void Corrupt_file_yields_defaults_and_reports_error()
    {
        var path = _tmp.WriteFile("settings.json", "{ nope");
        var store = new SettingsStore(path);

        var settings = store.Load();

        settings.CloseToTray.Should().BeTrue();
        store.LastLoadError.Should().NotBeNullOrEmpty();
    }
}
```

`tests/ReBackup.Core.Tests/Config/ConfigLocationTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Config;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Config;

public class ConfigLocationTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string AppData => _tmp.CreateDir("appdata");

    [Fact]
    public void ConfigPaths_builds_expected_layout()
    {
        var paths = new ConfigPaths(@"C:\cfg");

        paths.PlansDirectory.Should().Be(@"C:\cfg\plans");
        paths.LogsDirectory.Should().Be(@"C:\cfg\logs");
        paths.SettingsFile.Should().Be(@"C:\cfg\settings.json");
        paths.LogFileFor("abc").Should().Be(@"C:\cfg\logs\abc.jsonl");
    }

    [Fact]
    public void Resolve_without_pointer_uses_app_data_root()
    {
        ConfigLocation.Resolve(AppData).Root.Should().Be(AppData);
    }

    [Fact]
    public void Resolve_follows_pointer_file()
    {
        var appData = AppData;
        File.WriteAllText(Path.Combine(appData, "location.json"), """{ "configFolder": "E:\\cfg" }""");

        ConfigLocation.Resolve(appData).Root.Should().Be(@"E:\cfg");
    }

    [Fact]
    public void Resolve_ignores_corrupt_pointer_file()
    {
        var appData = AppData;
        File.WriteAllText(Path.Combine(appData, "location.json"), "garbage");

        ConfigLocation.Resolve(appData).Root.Should().Be(appData);
    }

    [Fact]
    public void ContainsConfiguration_detects_settings_or_plans()
    {
        var empty = _tmp.CreateDir("empty");
        var withSettings = _tmp.CreateDir("s");
        File.WriteAllText(Path.Combine(withSettings, "settings.json"), "{}");
        var withPlans = _tmp.CreateDir("p");
        _tmp.WriteFile(@"p\plans\x.json", "{}");

        ConfigLocation.ContainsConfiguration(empty).Should().BeFalse();
        ConfigLocation.ContainsConfiguration(withSettings).Should().BeTrue();
        ConfigLocation.ContainsConfiguration(withPlans).Should().BeTrue();
    }

    [Fact]
    public void Move_with_CopyCurrent_copies_files_and_writes_pointer()
    {
        var appData = AppData;
        var current = new ConfigPaths(appData);
        _tmp.WriteFile(@"appdata\settings.json", "{}");
        _tmp.WriteFile(@"appdata\plans\p1.json", "plan");
        _tmp.WriteFile(@"appdata\logs\p1.jsonl", "log");
        var newRoot = _tmp.PathOf("newcfg");

        var moved = ConfigLocation.Move(appData, current, newRoot, ConfigMoveMode.CopyCurrent);

        moved.Root.Should().Be(newRoot);
        File.ReadAllText(moved.SettingsFile).Should().Be("{}");
        File.ReadAllText(Path.Combine(moved.PlansDirectory, "p1.json")).Should().Be("plan");
        File.ReadAllText(moved.LogFileFor("p1")).Should().Be("log");
        File.Exists(Path.Combine(appData, "plans", "p1.json")).Should().BeTrue("old files are kept");
        ConfigLocation.Resolve(appData).Root.Should().Be(newRoot);
    }

    [Fact]
    public void Move_with_CopyCurrent_refuses_folder_that_already_has_configuration()
    {
        var appData = AppData;
        _tmp.WriteFile(@"existing\settings.json", "{}");

        var act = () => ConfigLocation.Move(appData, new ConfigPaths(appData), _tmp.PathOf("existing"), ConfigMoveMode.CopyCurrent);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Move_with_UseExisting_only_repoints()
    {
        var appData = AppData;
        _tmp.WriteFile(@"appdata\plans\mine.json", "mine");
        _tmp.WriteFile(@"existing\settings.json", "theirs");

        var moved = ConfigLocation.Move(appData, new ConfigPaths(appData), _tmp.PathOf("existing"), ConfigMoveMode.UseExisting);

        File.ReadAllText(moved.SettingsFile).Should().Be("theirs");
        File.Exists(Path.Combine(moved.PlansDirectory, "mine.json")).Should().BeFalse();
        ConfigLocation.Resolve(appData).Root.Should().Be(moved.Root);
    }

    [Fact]
    public void Moving_back_to_app_data_root_removes_pointer()
    {
        var appData = AppData;
        _tmp.WriteFile(@"appdata\settings.json", "{}");
        var elsewhere = ConfigLocation.Move(appData, new ConfigPaths(appData), _tmp.PathOf("other"), ConfigMoveMode.CopyCurrent);

        var back = ConfigLocation.Move(appData, elsewhere, appData, ConfigMoveMode.UseExisting);

        back.Root.Should().Be(appData);
        File.Exists(Path.Combine(appData, "location.json")).Should().BeFalse();
    }

    [Fact]
    public void Move_to_the_same_folder_is_a_no_op()
    {
        var appData = AppData;
        var current = new ConfigPaths(appData);

        ConfigLocation.Move(appData, current, appData + @"\", ConfigMoveMode.CopyCurrent).Should().Be(current);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~ConfigLocationTests"`
Expected: build error `The type or namespace name 'Settings' does not exist`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Config/ConfigPaths.cs`**

```csharp
namespace ReBackup.Core.Config;

public sealed record ConfigPaths(string Root)
{
    public string PlansDirectory => Path.Combine(Root, "plans");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LogFileFor(string planId) => Path.Combine(LogsDirectory, planId + ".jsonl");
}
```

- [ ] **Step 4: Implement `src/ReBackup.Core/Config/ConfigLocation.cs`**

```csharp
using System.Text.Json;
using ReBackup.Core.IO;
using ReBackup.Core.Json;

namespace ReBackup.Core.Config;

public enum ConfigMoveMode
{
    /// <summary>Copy settings, plans and logs into the new folder (it must not contain a configuration yet).</summary>
    CopyCurrent,
    /// <summary>Point at the new folder as-is without copying anything.</summary>
    UseExisting,
}

public static class ConfigLocation
{
    public const string PointerFileName = "location.json";

    public static string DefaultAppDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReBackup");

    public static ConfigPaths Resolve(string appDataRoot)
    {
        var pointerFile = Path.Combine(appDataRoot, PointerFileName);
        if (File.Exists(pointerFile))
        {
            try
            {
                var pointer = JsonSerializer.Deserialize<LocationPointer>(File.ReadAllText(pointerFile), JsonDefaults.Options);
                if (!string.IsNullOrWhiteSpace(pointer?.ConfigFolder))
                    return new ConfigPaths(pointer.ConfigFolder);
            }
            catch (JsonException)
            {
                // Fall back to the default location.
            }
        }
        return new ConfigPaths(appDataRoot);
    }

    public static bool ContainsConfiguration(string root)
    {
        var paths = new ConfigPaths(root);
        return File.Exists(paths.SettingsFile)
            || (Directory.Exists(paths.PlansDirectory) && Directory.EnumerateFiles(paths.PlansDirectory, "*.json").Any());
    }

    public static ConfigPaths Move(string appDataRoot, ConfigPaths current, string newRoot, ConfigMoveMode mode)
    {
        var target = new ConfigPaths(PathUtil.Normalize(newRoot));
        if (SamePath(current.Root, target.Root))
            return current;

        if (mode == ConfigMoveMode.CopyCurrent)
        {
            if (ContainsConfiguration(target.Root))
                throw new InvalidOperationException("The chosen folder already contains a ReBackup configuration.");

            Directory.CreateDirectory(target.PlansDirectory);
            Directory.CreateDirectory(target.LogsDirectory);
            if (File.Exists(current.SettingsFile))
                File.Copy(current.SettingsFile, target.SettingsFile);
            CopyFiles(current.PlansDirectory, target.PlansDirectory);
            CopyFiles(current.LogsDirectory, target.LogsDirectory);
        }

        Directory.CreateDirectory(appDataRoot);
        var pointerFile = Path.Combine(appDataRoot, PointerFileName);
        if (SamePath(appDataRoot, target.Root))
            File.Delete(pointerFile);
        else
            AtomicFile.WriteAllText(pointerFile, JsonSerializer.Serialize(new LocationPointer(target.Root), JsonDefaults.Options));

        return target;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static void CopyFiles(string fromDir, string toDir)
    {
        if (!Directory.Exists(fromDir))
            return;
        foreach (var file in Directory.EnumerateFiles(fromDir))
            File.Copy(file, Path.Combine(toDir, Path.GetFileName(file)));
    }

    internal sealed record LocationPointer(string ConfigFolder);
}
```

- [ ] **Step 5: Implement `src/ReBackup.Core/Settings/AppSettings.cs`**

```csharp
namespace ReBackup.Core.Settings;

public sealed class AppSettings
{
    public static readonly IReadOnlyList<string> BuiltInIgnoreDefaults =
        ["Thumbs.db", "desktop.ini", "$RECYCLE.BIN/", "System Volume Information/"];

    public List<string> DefaultIgnorePatterns { get; set; } = [.. BuiltInIgnoreDefaults];
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
}
```

- [ ] **Step 6: Implement `src/ReBackup.Core/Settings/SettingsStore.cs`**

```csharp
using System.Text.Json;
using ReBackup.Core.IO;
using ReBackup.Core.Json;

namespace ReBackup.Core.Settings;

public sealed class SettingsStore
{
    public SettingsStore(string settingsFile) => SettingsFile = settingsFile;

    public string SettingsFile { get; }

    /// <summary>Set when the last <see cref="Load"/> found a corrupt file and fell back to defaults.</summary>
    public string? LastLoadError { get; private set; }

    public AppSettings Load()
    {
        LastLoadError = null;
        if (!File.Exists(SettingsFile))
            return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonDefaults.Options)
                ?? new AppSettings();
        }
        catch (JsonException ex)
        {
            LastLoadError = ex.Message;
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings) =>
        AtomicFile.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonDefaults.Options));
}
```

- [ ] **Step 7: Run all Core tests**

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: PASS (all tests).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): add settings store and config folder location" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Main window with plan list and General tab

**Files:**
- Create: `src/ReBackup.App/Services/IDialogService.cs`, `src/ReBackup.App/Services/WpfDialogService.cs`, `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/ViewModels/MainViewModel.cs`
- Modify: `src/ReBackup.App/MainWindow.xaml`, `src/ReBackup.App/MainWindow.xaml.cs`, `src/ReBackup.App/App.xaml`, `src/ReBackup.App/App.xaml.cs`
- Modify: `docs/superpowers/specs/2026-09-30-rebackup-design.md` (§10.1, the sentence about switching plans)

**Interfaces:**
- Consumes: `PlanStore`, `PlanValidator`, `BackupPlan`, `ConfigPaths`, `ConfigLocation`, `SettingsStore`
- Produces (used by Tasks 7–8 and later phases):
  - `IDialogService` with `bool Confirm(string title, string message)`, `bool? AskYesNoCancel(string title, string message)`, `string? PickFolder(string title, string? initialFolder)`, `void ShowError(string title, string message)`, `void ShowInfo(string title, string message)`
  - `PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans)` with `Id`, `Name`, `Source`, `Target`, `Enabled`, `FreeSpaceByRetention`, `IsDirty`, `IsNew`, `Errors`, `DisplayName`, `ToPlan()`, `TrySave(PlanStore)`, `Revert()`, `ReplaceSaved(BackupPlan)`, `MarkAsNew()`, `Validate()`
  - `MainViewModel(PlanStore store, ConfigPaths paths, IDialogService dialogs, Action openSettings)` with `Plans`, `SelectedPlan`, `StatusMessage`, `HasUnsavedChanges`, `UnsavedPlanNames`, `ReloadFromDisk()`, and the commands `NewPlanCommand`, `DeletePlanCommand`, `SaveCommand`, `RevertCommand`, `BrowseSourceCommand`, `BrowseTargetCommand`, `OpenSettingsCommand`

The UI has no automated tests. View models stay thin, and all rules live in the tested Core. Verification is the manual checklist in Step 9.

- [ ] **Step 1: Create `src/ReBackup.App/Services/IDialogService.cs`**

```csharp
namespace ReBackup.App.Services;

public interface IDialogService
{
    bool Confirm(string title, string message);

    /// <summary>Yes → true, No → false, Cancel → null.</summary>
    bool? AskYesNoCancel(string title, string message);

    string? PickFolder(string title, string? initialFolder);

    void ShowError(string title, string message);

    void ShowInfo(string title, string message);
}
```

- [ ] **Step 2: Create `src/ReBackup.App/Services/WpfDialogService.cs`**

```csharp
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace ReBackup.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    public bool Confirm(string title, string message) =>
        Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public bool? AskYesNoCancel(string title, string message) =>
        Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };

    public string? PickFolder(string title, string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder))
            dialog.InitialDirectory = initialFolder;

        var owner = Owner;
        var ok = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return ok == true ? dialog.FolderName : null;
    }

    public void ShowError(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        var owner = Owner;
        return owner is null
            ? MessageBox.Show(text, caption, buttons, image)
            : MessageBox.Show(owner, text, caption, buttons, image);
    }
}
```

- [ ] **Step 3: Create `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

/// <summary>Editable copy of one plan. Edits stay in memory (per plan) until saved or reverted.</summary>
public sealed partial class PlanEditorViewModel : ObservableObject
{
    private readonly Func<IEnumerable<BackupPlan>> _allPlans;
    private BackupPlan _saved;
    private bool _loading;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _freeSpaceByRetention;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DisplayName))] private bool _isDirty;
    [ObservableProperty] private bool _isNew;
    [ObservableProperty] private IReadOnlyList<string> _errors = [];

    public PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans)
    {
        _saved = plan.Clone();
        _allPlans = allPlans;
        IsNew = isNew;
        LoadFrom(_saved);
        IsDirty = isNew;
        Validate();
    }

    public string Id => _saved.Id;

    public string DisplayName =>
        (IsDirty ? "• " : "") + (string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name);

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayName));
        Touch();
    }

    partial void OnSourceChanged(string value) => Touch();
    partial void OnTargetChanged(string value) => Touch();
    partial void OnEnabledChanged(bool value) => Touch();
    partial void OnFreeSpaceByRetentionChanged(bool value) => Touch();

    public BackupPlan ToPlan()
    {
        var plan = _saved.Clone();
        plan.Name = Name;
        plan.Source = Source;
        plan.Target = Target;
        plan.Enabled = Enabled;
        plan.FreeSpaceByRetention = FreeSpaceByRetention;
        return plan;
    }

    public void Validate() => Errors = PlanValidator.Validate(ToPlan(), _allPlans());

    public bool TrySave(PlanStore store)
    {
        Validate();
        if (Errors.Count > 0)
            return false;

        var plan = ToPlan();
        store.Save(plan);
        _saved = plan;
        IsDirty = false;
        IsNew = false;
        return true;
    }

    public void Revert()
    {
        LoadFrom(_saved);
        IsDirty = IsNew;
        Validate();
    }

    /// <summary>Takes a newer version from disk (only called when there are no unsaved edits).</summary>
    public void ReplaceSaved(BackupPlan plan)
    {
        _saved = plan.Clone();
        LoadFrom(_saved);
        IsDirty = false;
        Validate();
    }

    /// <summary>The file was deleted on disk while this editor had unsaved edits; saving recreates it.</summary>
    public void MarkAsNew() => IsNew = true;

    private void Touch()
    {
        if (_loading)
            return;
        IsDirty = true;
        Validate();
    }

    private void LoadFrom(BackupPlan plan)
    {
        _loading = true;
        try
        {
            Name = plan.Name;
            Source = plan.Source;
            Target = plan.Target;
            Enabled = plan.Enabled;
            FreeSpaceByRetention = plan.FreeSpaceByRetention;
        }
        finally
        {
            _loading = false;
        }
    }
}
```

- [ ] **Step 4: Create `src/ReBackup.App/ViewModels/MainViewModel.cs`**

```csharp
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly PlanStore _store;
    private readonly ConfigPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly Action _openSettings;

    [ObservableProperty] private PlanEditorViewModel? _selectedPlan;
    [ObservableProperty] private string? _statusMessage;

    public MainViewModel(PlanStore store, ConfigPaths paths, IDialogService dialogs, Action openSettings)
    {
        _store = store;
        _paths = paths;
        _dialogs = dialogs;
        _openSettings = openSettings;

        var result = _store.LoadAll();
        foreach (var plan in result.Plans)
            Plans.Add(new PlanEditorViewModel(plan, isNew: false, AllPlans));
        SelectedPlan = Plans.FirstOrDefault();
        StatusMessage = LoadErrorText(result) ?? $"Configuration: {_paths.Root}";
    }

    public ObservableCollection<PlanEditorViewModel> Plans { get; } = [];

    public bool HasUnsavedChanges => Plans.Any(p => p.IsDirty);

    public IEnumerable<string> UnsavedPlanNames =>
        Plans.Where(p => p.IsDirty).Select(p => string.IsNullOrWhiteSpace(p.Name) ? "(unnamed)" : p.Name);

    public void ReloadFromDisk()
    {
        var result = _store.LoadAll();
        var loaded = result.Plans.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var editor in Plans.ToList())
        {
            if (loaded.Remove(editor.Id, out var plan))
            {
                if (!editor.IsDirty)
                    editor.ReplaceSaved(plan);
            }
            else if (!editor.IsNew)
            {
                if (editor.IsDirty)
                    editor.MarkAsNew();
                else
                    Plans.Remove(editor);
            }
        }

        foreach (var plan in loaded.Values)
            Plans.Add(new PlanEditorViewModel(plan, isNew: false, AllPlans));

        if (SelectedPlan is null || !Plans.Contains(SelectedPlan))
            SelectedPlan = Plans.FirstOrDefault();

        StatusMessage = LoadErrorText(result) ?? "Plans reloaded after a change on disk.";
    }

    [RelayCommand]
    private void NewPlan()
    {
        var editor = new PlanEditorViewModel(new BackupPlan { Name = UniqueName("New plan") }, isNew: true, AllPlans);
        Plans.Add(editor);
        SelectedPlan = editor;
    }

    [RelayCommand]
    private void DeletePlan()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        if (!editor.IsNew)
        {
            if (!_dialogs.Confirm("Delete plan",
                    $"Delete plan \"{editor.Name}\"?\n\nBackups already stored in the target are not touched."))
                return;
            var deleteLog = _dialogs.AskYesNoCancel("Delete plan", "Also delete this plan's run history?");
            if (deleteLog is null)
                return;

            try
            {
                _store.Delete(editor.Id);
                var logFile = _paths.LogFileFor(editor.Id);
                if (deleteLog == true && File.Exists(logFile))
                    File.Delete(logFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _dialogs.ShowError("Delete plan", ex.Message);
                return;
            }
        }

        var index = Plans.IndexOf(editor);
        Plans.Remove(editor);
        SelectedPlan = Plans.Count == 0 ? null : Plans[Math.Min(index, Plans.Count - 1)];
    }

    [RelayCommand]
    private void Save()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        try
        {
            StatusMessage = editor.TrySave(_store)
                ? $"Saved \"{editor.Name}\"."
                : "Not saved: fix the errors shown in the plan.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError("Save failed", ex.Message);
        }
    }

    [RelayCommand]
    private void Revert()
    {
        var editor = SelectedPlan;
        if (editor is null)
            return;

        if (editor.IsNew)
        {
            Plans.Remove(editor);
            SelectedPlan = Plans.FirstOrDefault();
            return;
        }
        editor.Revert();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (SelectedPlan is { } editor && _dialogs.PickFolder("Choose source folder", editor.Source) is { } folder)
            editor.Source = folder;
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        if (SelectedPlan is { } editor && _dialogs.PickFolder("Choose target folder", editor.Target) is { } folder)
            editor.Target = folder;
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    private IEnumerable<BackupPlan> AllPlans() => Plans.Select(p => p.ToPlan());

    private string UniqueName(string baseName)
    {
        var names = Plans.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
            return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!names.Contains(candidate))
                return candidate;
        }
    }

    private static string? LoadErrorText(PlanLoadResult result) =>
        result.Errors.Count == 0
            ? null
            : $"{result.Errors.Count} plan file(s) could not be read: " +
              string.Join("; ", result.Errors.Select(e => $"{Path.GetFileName(e.FilePath)}: {e.Message}"));
}
```

- [ ] **Step 5: Replace `src/ReBackup.App/MainWindow.xaml`**

```xml
<Window x:Class="ReBackup.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:ReBackup.App.ViewModels"
        Title="ReBackup" Width="1100" Height="700" MinWidth="800" MinHeight="500">
    <Window.InputBindings>
        <KeyBinding Gesture="Ctrl+S" Command="{Binding SaveCommand}" />
    </Window.InputBindings>

    <Window.Resources>
        <DataTemplate DataType="{x:Type vm:PlanEditorViewModel}">
            <TabControl>
                <TabItem Header="General">
                    <Grid Margin="12">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="Auto" />
                            <RowDefinition Height="*" />
                        </Grid.RowDefinitions>

                        <TextBlock Grid.Row="0" Text="Name" VerticalAlignment="Center" Margin="0,0,12,8" />
                        <TextBox Grid.Row="0" Grid.Column="1" Grid.ColumnSpan="2" Margin="0,0,0,8"
                                 Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}" />

                        <TextBlock Grid.Row="1" Text="Source" VerticalAlignment="Center" Margin="0,0,12,8" />
                        <TextBox Grid.Row="1" Grid.Column="1" Margin="0,0,8,8"
                                 Text="{Binding Source, UpdateSourceTrigger=PropertyChanged}" />
                        <Button Grid.Row="1" Grid.Column="2" Content="Browse…" Padding="10,2" Margin="0,0,0,8"
                                Command="{Binding DataContext.BrowseSourceCommand, RelativeSource={RelativeSource AncestorType=Window}}" />

                        <TextBlock Grid.Row="2" Text="Target" VerticalAlignment="Center" Margin="0,0,12,8" />
                        <TextBox Grid.Row="2" Grid.Column="1" Margin="0,0,8,8"
                                 Text="{Binding Target, UpdateSourceTrigger=PropertyChanged}" />
                        <Button Grid.Row="2" Grid.Column="2" Content="Browse…" Padding="10,2" Margin="0,0,0,8"
                                Command="{Binding DataContext.BrowseTargetCommand, RelativeSource={RelativeSource AncestorType=Window}}" />

                        <CheckBox Grid.Row="3" Grid.Column="1" Margin="0,4,0,8"
                                  Content="Enabled (scheduled runs)" IsChecked="{Binding Enabled}" />
                        <CheckBox Grid.Row="4" Grid.Column="1" Margin="0,0,0,8"
                                  Content="When the target is full, first delete versions that retention would delete anyway"
                                  IsChecked="{Binding FreeSpaceByRetention}" />

                        <ItemsControl Grid.Row="5" Grid.ColumnSpan="3" Margin="0,8,0,0" ItemsSource="{Binding Errors}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <TextBlock Text="{Binding}" Foreground="Firebrick" />
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </Grid>
                </TabItem>
            </TabControl>
        </DataTemplate>
    </Window.Resources>

    <DockPanel>
        <StatusBar DockPanel.Dock="Bottom">
            <TextBlock Text="{Binding StatusMessage}" TextTrimming="CharacterEllipsis" />
        </StatusBar>

        <Grid Margin="8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="280" />
                <ColumnDefinition Width="8" />
                <ColumnDefinition Width="*" />
            </Grid.ColumnDefinitions>

            <DockPanel Grid.Column="0">
                <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" Margin="0,8,0,0">
                    <Button Content="New plan" Padding="10,4" Command="{Binding NewPlanCommand}" />
                    <Button Content="Delete" Padding="10,4" Margin="8,0,0,0" Command="{Binding DeletePlanCommand}" />
                    <Button Content="Settings…" Padding="10,4" Margin="8,0,0,0" Command="{Binding OpenSettingsCommand}" />
                </StackPanel>
                <ListBox ItemsSource="{Binding Plans}" SelectedItem="{Binding SelectedPlan}">
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Margin="2,4">
                                <TextBlock Text="{Binding DisplayName}" FontWeight="SemiBold" />
                                <TextBlock Text="{Binding Source}" Foreground="Gray" FontSize="11"
                                           TextTrimming="CharacterEllipsis" />
                            </StackPanel>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </DockPanel>

            <DockPanel Grid.Column="2">
                <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,8,0,0">
                    <Button Content="Revert" Padding="14,4" Command="{Binding RevertCommand}"
                            IsEnabled="{Binding SelectedPlan.IsDirty, FallbackValue=False}" />
                    <Button Content="Save" Padding="14,4" Margin="8,0,0,0" Command="{Binding SaveCommand}"
                            IsEnabled="{Binding SelectedPlan.IsDirty, FallbackValue=False}" />
                </StackPanel>
                <ContentControl Content="{Binding SelectedPlan}" />
            </DockPanel>
        </Grid>
    </DockPanel>
</Window>
```

- [ ] **Step 6: Replace `src/ReBackup.App/MainWindow.xaml.cs`**

```csharp
using System.Windows;

namespace ReBackup.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 7: Make the application class the composition root**

`src/ReBackup.App/App.xaml` (remove `StartupUri`, explicit shutdown):

```xml
<Application x:Class="ReBackup.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources />
</Application>
```

`src/ReBackup.App/App.xaml.cs` (Task 7 extends this with the tray, and Task 8 with settings):

```csharp
using System.ComponentModel;
using System.IO;
using System.Windows;
using ReBackup.App.Services;
using ReBackup.App.ViewModels;
using ReBackup.Core.Config;
using ReBackup.Core.Plans;
using ReBackup.Core.Settings;

namespace ReBackup.App;

public partial class App : Application
{
    private readonly IDialogService _dialogs = new WpfDialogService();
    private string _appDataRoot = "";
    private ConfigPaths _paths = null!;
    private SettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;
    private PlanStore _planStore = null!;
    private MainViewModel _mainViewModel = null!;
    private MainWindow _window = null!;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _appDataRoot = ConfigLocation.DefaultAppDataRoot;
        _paths = ConfigLocation.Resolve(_appDataRoot);
        Directory.CreateDirectory(_paths.PlansDirectory);
        Directory.CreateDirectory(_paths.LogsDirectory);

        _settingsStore = new SettingsStore(_paths.SettingsFile);
        _settings = _settingsStore.Load();
        if (_settingsStore.LastLoadError is { } error)
            _dialogs.ShowError("Settings", $"settings.json could not be read, defaults are used.\n\n{error}");

        _planStore = new PlanStore(_paths.PlansDirectory);
        _mainViewModel = new MainViewModel(_planStore, _paths, _dialogs, ShowSettings);
        _planStore.ExternalChange += (_, _) => Dispatcher.InvokeAsync(_mainViewModel.ReloadFromDisk);
        _planStore.StartWatching();

        _window = new MainWindow { DataContext = _mainViewModel };
        _window.Closing += OnMainWindowClosing;
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void ShowSettings()
    {
        // Implemented in Task 8.
    }

    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        e.Cancel = true;
        ExitApp();
    }

    private bool ConfirmDiscardUnsaved()
    {
        if (!_mainViewModel.HasUnsavedChanges)
            return true;
        return _dialogs.Confirm("Unsaved changes",
            "These plans have unsaved changes:\n\n" + string.Join("\n", _mainViewModel.UnsavedPlanNames) +
            "\n\nDiscard the changes?");
    }

    private void ExitApp()
    {
        if (!ConfirmDiscardUnsaved())
            return;

        _exitRequested = true;
        _planStore.Dispose();
        Shutdown();
    }
}
```

Task 8 replaces the empty `ShowSettings` body. That is the only stub in this plan, and it exists so this task compiles on its own.

- [ ] **Step 8: Update the spec**

In `docs/superpowers/specs/2026-09-30-rebackup-design.md` §10.1, replace the sentence `Switching plans with unsaved edits asks first.` with:

```
Unsaved edits are kept per plan while switching between plans; exiting (or restarting after a config-folder change) with unsaved edits asks first.
```

- [ ] **Step 9: Build, run all tests, verify manually**

Run: `dotnet build` then `dotnet test tests/ReBackup.Core.Tests`
Expected: 0 build errors; all tests PASS.

Run: `dotnet run --project src/ReBackup.App`
Check each item:
1. The window opens with an empty plan list, and the status bar shows `Configuration: C:\Users\<you>\AppData\Roaming\ReBackup`.
2. **New plan** adds "• New plan" and selects it. Errors show "Source folder is required." and "Target folder is required."
3. Browse a source and a target. The errors disappear. Choosing a target inside the source shows "Target must not be inside the source."
4. **Save** (or Ctrl+S) removes the "•", and `%AppData%\ReBackup\plans\<id>.json` exists with camelCase content.
5. Edit the name, switch to another plan and back. The edit is still there, marked with "•". **Revert** restores the saved name.
6. Add a second plan with the same name (different case). "Another plan is already named …" is shown, and Save is refused with a status message.
7. Edit the JSON file in a text editor (change the name) and save it. Within about a second the list shows the new name.
8. **Delete** asks twice (confirm, then history Yes/No/Cancel). The JSON file is removed.
9. With an unsaved edit, close the window. You're asked about discarding; **No** keeps the app open.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(app): plan list with General tab, save, revert and delete" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Tray icon, close-to-tray, start minimized

**Files:**
- Modify: `src/ReBackup.App/App.xaml.cs`

**Interfaces:**
- Consumes: `AppSettings.CloseToTray`, `MainViewModel`, H.NotifyIcon (`TaskbarIcon`, `GeneratedIconSource`)
- Produces: `App.CreateTrayMenuItem(string header, Action action) → MenuItem` (Phase 3/5 add "Run plan ▸" and "Pause scheduler" to the tray menu through `_trayMenu`). The command-line switch `--minimized` starts with the window hidden.

- [ ] **Step 1: Add tray fields and usings to `App.xaml.cs`**

Add these usings:

```csharp
using System.Windows.Controls;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
```

Add these fields next to the existing ones:

```csharp
    private TaskbarIcon? _tray;
    private readonly ContextMenu _trayMenu = new();
```

- [ ] **Step 2: Create the tray icon and honour `--minimized`**

In `OnStartup`, replace the final line `ShowMainWindow();` with:

```csharp
        CreateTrayIcon();
        if (!e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            ShowMainWindow();
```

Add these methods to `App`:

```csharp
    private void CreateTrayIcon()
    {
        _trayMenu.Items.Add(CreateTrayMenuItem("Open", ShowMainWindow));
        _trayMenu.Items.Add(new Separator());
        _trayMenu.Items.Add(CreateTrayMenuItem("Exit", ExitApp));

        _tray = new TaskbarIcon
        {
            ToolTipText = "ReBackup",
            ContextMenu = _trayMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(ShowMainWindow),
            IconSource = new GeneratedIconSource
            {
                Text = "R",
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)),
                FontWeight = FontWeights.Bold,
            },
        };
        // Efficiency mode would throttle the process while hidden, which would slow scheduled backups.
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    internal static MenuItem CreateTrayMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
```

- [ ] **Step 3: Close-to-tray and tray disposal**

Replace `OnMainWindowClosing` and `ExitApp` with:

```csharp
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
            return;

        e.Cancel = true;
        if (_settings.CloseToTray)
            _window.Hide();
        else
            ExitApp();
    }

    private void ExitApp()
    {
        if (!ConfirmDiscardUnsaved())
            return;

        _exitRequested = true;
        _tray?.Dispose();
        _planStore.Dispose();
        Shutdown();
    }
```

- [ ] **Step 4: Build and verify manually**

Run: `dotnet build`. Expected: 0 errors. If `GeneratedIconSource` or `ForceCreate(enablesEfficiencyMode:)` doesn't compile, run `dotnet list src/ReBackup.App package` to see the installed H.NotifyIcon.Wpf version, and adapt to that version's API (the package README on nuget.org lists the `GeneratedIconSource` properties). Keep the behaviour: a white "R" on blue, and efficiency mode off.

Run: `dotnet run --project src/ReBackup.App`
1. A blue "R" icon appears in the notification area.
2. Closing the window hides it, and the process keeps running. Left-clicking the tray icon shows the window again.
3. Tray right-click → **Exit** quits (asking first if there are unsaved edits), and the icon disappears.
4. `dotnet run --project src/ReBackup.App -- --minimized` starts with only the tray icon; **Open** shows the window.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): tray icon with close-to-tray and --minimized start" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Settings dialog, start with Windows, config-folder move

**Files:**
- Create: `src/ReBackup.App/Services/StartupRegistration.cs`, `src/ReBackup.App/ViewModels/SettingsViewModel.cs`, `src/ReBackup.App/SettingsWindow.xaml`, `src/ReBackup.App/SettingsWindow.xaml.cs`
- Modify: `src/ReBackup.App/App.xaml.cs`

**Interfaces:**
- Consumes: `SettingsStore`, `AppSettings`, `ConfigLocation.Move/ContainsConfiguration`, `ConfigMoveMode`, `IDialogService`
- Produces: `StartupRegistration.Apply(bool enabled)`; `SettingsViewModel(SettingsStore store, AppSettings settings, ConfigPaths paths, string appDataRoot, IDialogService dialogs, Func<bool> confirmRestart)` with `ConfigFolder`, `DefaultIgnorePatterns` (multiline text), `CloseToTray`, `StartWithWindows`, `RestartRequired`, `event EventHandler<bool>? CloseRequested`, and the commands `ChangeConfigFolderCommand`, `SaveCommand`, `CancelCommand`

- [ ] **Step 1: Create `src/ReBackup.App/Services/StartupRegistration.cs`**

```csharp
using Microsoft.Win32;

namespace ReBackup.App.Services;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ReBackup";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
```

- [ ] **Step 2: Create `src/ReBackup.App/ViewModels/SettingsViewModel.cs`**

```csharp
using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.App.Services;
using ReBackup.Core.Config;
using ReBackup.Core.Settings;

namespace ReBackup.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly string _appDataRoot;
    private readonly IDialogService _dialogs;
    private readonly Func<bool> _confirmRestart;
    private ConfigPaths _paths;

    [ObservableProperty] private string _configFolder = "";
    [ObservableProperty] private string _defaultIgnorePatterns = "";
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;

    public SettingsViewModel(SettingsStore store, AppSettings settings, ConfigPaths paths, string appDataRoot,
        IDialogService dialogs, Func<bool> confirmRestart)
    {
        _store = store;
        _settings = settings;
        _paths = paths;
        _appDataRoot = appDataRoot;
        _dialogs = dialogs;
        _confirmRestart = confirmRestart;

        ConfigFolder = paths.Root;
        DefaultIgnorePatterns = string.Join(Environment.NewLine, settings.DefaultIgnorePatterns);
        CloseToTray = settings.CloseToTray;
        StartWithWindows = settings.StartWithWindows;
    }

    /// <summary>Raised with the dialog result (true = saved).</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool RestartRequired { get; private set; }

    [RelayCommand]
    private void ChangeConfigFolder()
    {
        var folder = _dialogs.PickFolder("Choose configuration folder", ConfigFolder);
        if (folder is null)
            return;

        var mode = ConfigMoveMode.CopyCurrent;
        if (ConfigLocation.ContainsConfiguration(folder))
        {
            if (!_dialogs.Confirm("Configuration folder",
                    "That folder already contains a ReBackup configuration.\n\nSwitch to it without copying the current plans?"))
                return;
            mode = ConfigMoveMode.UseExisting;
        }

        if (!_confirmRestart())
            return;

        try
        {
            _paths = ConfigLocation.Move(_appDataRoot, _paths, folder, mode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _dialogs.ShowError("Configuration folder", ex.Message);
            return;
        }

        ConfigFolder = _paths.Root;
        RestartRequired = true;
        _dialogs.ShowInfo("Configuration folder", "ReBackup will now restart to use the new configuration folder.");
        CloseRequested?.Invoke(this, false);
    }

    [RelayCommand]
    private void Save()
    {
        _settings.DefaultIgnorePatterns = DefaultIgnorePatterns
            .Split('\n')
            .Select(line => line.TrimEnd('\r', ' ', '\t'))
            .Where(line => line.Length > 0)
            .ToList();
        _settings.CloseToTray = CloseToTray;
        _settings.StartWithWindows = StartWithWindows;

        try
        {
            _store.Save(_settings);
            StartupRegistration.Apply(StartWithWindows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            _dialogs.ShowError("Settings", ex.Message);
            return;
        }

        CloseRequested?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
```

Note: `Save` changes the shared `AppSettings` instance that `App` holds, so close-to-tray takes effect immediately without a restart.

- [ ] **Step 3: Create `src/ReBackup.App/SettingsWindow.xaml`**

```xml
<Window x:Class="ReBackup.App.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="ReBackup settings" Width="560" Height="480" MinWidth="460" MinHeight="400"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False">
    <Grid Margin="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>

        <DockPanel Grid.Row="0" Margin="0,0,0,12">
            <TextBlock DockPanel.Dock="Top" Text="Configuration folder" FontWeight="SemiBold" Margin="0,0,0,4" />
            <Button DockPanel.Dock="Right" Content="Change…" Padding="10,2" Margin="8,0,0,0"
                    Command="{Binding ChangeConfigFolderCommand}" />
            <TextBox Text="{Binding ConfigFolder, Mode=OneWay}" IsReadOnly="True" />
        </DockPanel>

        <TextBlock Grid.Row="1" Text="Default ignore patterns (one per line, gitignore syntax)"
                   FontWeight="SemiBold" Margin="0,0,0,4" />
        <TextBox Grid.Row="2" AcceptsReturn="True" VerticalScrollBarVisibility="Auto" FontFamily="Consolas"
                 Text="{Binding DefaultIgnorePatterns, UpdateSourceTrigger=PropertyChanged}" />

        <CheckBox Grid.Row="3" Margin="0,12,0,0" Content="Closing the window minimizes to the tray"
                  IsChecked="{Binding CloseToTray}" />
        <CheckBox Grid.Row="4" Margin="0,8,0,0" Content="Start with Windows (minimized to tray)"
                  IsChecked="{Binding StartWithWindows}" />

        <StackPanel Grid.Row="5" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Cancel" Padding="14,4" IsCancel="True" Command="{Binding CancelCommand}" />
            <Button Content="Save" Padding="14,4" Margin="8,0,0,0" IsDefault="True" Command="{Binding SaveCommand}" />
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 4: Create `src/ReBackup.App/SettingsWindow.xaml.cs`**

```csharp
using System.Windows;
using ReBackup.App.ViewModels;

namespace ReBackup.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, result) => DialogResult = result;
    }
}
```

- [ ] **Step 5: Wire settings and restart into `App.xaml.cs`**

Add this using:

```csharp
using System.Diagnostics;
```

Replace the empty `ShowSettings` with the following, and add `Restart`:

```csharp
    private void ShowSettings()
    {
        var viewModel = new SettingsViewModel(_settingsStore, _settings, _paths, _appDataRoot, _dialogs, ConfirmDiscardUnsaved);
        var window = new SettingsWindow(viewModel);
        if (_window.IsVisible)
            window.Owner = _window;
        window.ShowDialog();

        if (viewModel.RestartRequired)
            Restart();
    }

    private void Restart()
    {
        _exitRequested = true;
        _tray?.Dispose();
        _planStore.Dispose();
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false });
        Shutdown();
    }
```

(`ConfirmDiscardUnsaved` is passed as `confirmRestart`, so unsaved plan edits are confirmed before the folder is switched.)

- [ ] **Step 6: Build, run all tests, verify manually**

Run: `dotnet build` then `dotnet test tests/ReBackup.Core.Tests`
Expected: 0 errors; all tests PASS.

Run: `dotnet run --project src/ReBackup.App`
1. **Settings…** opens the dialog. The folder shows `%AppData%\ReBackup`, the patterns show the 4 defaults, close-to-tray is checked, and start-with-Windows is unchecked.
2. Uncheck close-to-tray and Save. Closing the main window now exits the app.
3. Restart, check start-with-Windows and Save. `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ReBackup` shows `"…\ReBackup.App.exe" --minimized`. Uncheck and Save, and the value is gone.
4. Edit the patterns (add `*.bak`) and Save. `settings.json` contains `"*.bak"`.
5. **Change…** to an empty folder, e.g. `E:\ReBackupCfg`. The app restarts, the status bar shows the new folder, the plans are still listed, and `%AppData%\ReBackup\location.json` points to it.
6. **Change…** back to `%AppData%\ReBackup`. You're asked "already contains … switch without copying", then Yes → restart, and `location.json` is removed.
7. Set everything back to its defaults (close-to-tray on, start-with-Windows off).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(app): settings dialog with start-with-Windows and config folder move" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Next plans

After Phase 1 is merged, the next plan is written against the real code: `docs/superpowers/plans/YYYY-MM-DD-rebackup-phase2-ignore-preview.md` (spec §6, §10.2 tab 3; build phase 2). Later phases follow the order in spec §13.

## Carry-forward from Phase 1 execution (final review triage)

Precondition for Phase 3 (runner/queue):
- Single-instance guard (named mutex + activate first instance), with a restart handoff because `Restart()` starts the new process before the old one exits. Two instances would run the same plan twice.

Early in Phase 2:
- Fix `ContentControl` re-instantiating the `TabControl` on every selection change (the selected tab resets) before adding the second tab.
- Tighten `PlanStoreTests.Unknown_properties_survive_load_and_save` to compare the JSON structure, not substrings.
- Consider moving the `MainViewModel.ReloadFromDisk` merge decision into a pure, tested Core function.
- A dirty editor keeps a stale `_saved`. Saving then overwrites external edits to sections it doesn't show (triggers/ignore/retention). Decide the policy once those sections become editable.
- `PlanValidator` calls `Directory.Exists` on the UI thread for every plan on each Name change, so a slow or unreachable share stalls typing.

Later / nice to have:
- Global `DispatcherUnhandledException` handler that logs to `<config>/logs/app.log`.
- `FileSystemWatcher.Error` → raise `ExternalChange`; stale own-write markers after a failed Delete.
- `WpfDialogService.Owner` is null while hidden to tray (relevant once dialogs can come from the tray in Phases 3/5).
- `OnSessionEnding` dereferences `_planStore` if the session ends while the startup config dialog is open.
- Config move: `CopyCurrent` is not atomic (a partial copy then blocks a retry), and copying a log fails if a same-named log already exists in the target.
- Run-key path goes stale if the exe moves; `AtomicFile` uses a fixed `.tmp` name; `AppSettings` has no `[JsonExtensionData]`; newly appeared plans are appended unsorted; the dirty flag is one-way; reserved device names (CON, NUL) are not rejected in plan names (harmless inside version folder names).
