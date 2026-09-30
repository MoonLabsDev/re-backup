# ReBackup Phase 2 — Ignore & Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Each plan gets an "Ignore & Preview" tab: a gitignore-style pattern editor, a quick index of the source shown as a size tree and a treemap, and live re-evaluation of what would be saved or ignored while the patterns are edited.

**Architecture:** All logic is pure and lives in `ReBackup.Core`: `IgnorePattern`/`IgnoreMatcher` (gitignore semantics), `SourceIndexer` (one scan into an in-memory tree), `IndexEvaluator` (applies a matcher to a cached index without rescanning) and `TreemapLayout` (squarified layout). The WPF layer adds a flattened, virtualized tree list (`PreviewTreeViewModel`), a preview view model that debounces re-evaluation, and a custom-drawn `TreemapControl`.

**Tech Stack:** .NET 9, WPF, CommunityToolkit.Mvvm 8.4.2, System.Text.RegularExpressions, xUnit, FluentAssertions 7.0.0.

**Spec:** `docs/superpowers/specs/2026-09-30-rebackup-design.md` (this plan covers §4.2 `ignore` section, §5 `IgnoreMatcher`/`SourceIndexer`/`IndexEvaluator`, §6, §10.2 tab 3, and build phase 2 of §13)

## Global Constraints

- Target frameworks: `net9.0` for Core and Tests, `net9.0-windows` for App. Nullable and implicit usings are on solution-wide. Builds must stay at 0 warnings.
- Relative paths inside the index and the matcher use forward slashes, have no leading or trailing slash, and the source root is `""`.
- Ignore matching is case-insensitive.
- Pattern precedence, lowest to highest: global defaults → plan patterns → nested `.backupignore` files from the root down. The last matching pattern wins. A file cannot be re-included when a parent folder is excluded.
- Pattern origins are reported as `Global defaults`, `Plan`, or the relative path of the nested file (`.backupignore`, `sub/.backupignore`).
- Nested ignore files are named `.backupignore`. They are ordinary files and are backed up themselves.
- Plan JSON `ignore` section: `{ "useGlobalDefaults": true, "honorNestedFiles": true, "patterns": [] }` (these are also the defaults).
- Pattern edits re-evaluate against the cached index after a 300 ms debounce, with no rescan. The index is cached per plan for the session.
- JSON options only from `JsonDefaults.Options`. Config writes only via `AtomicFile.WriteAllText`.
- FluentAssertions stays pinned to 7.0.0.
- Every commit message ends with a `Co-Authored-By: Claude <model> <noreply@anthropic.com>` trailer naming the model that authored the commit.
- The App has no automated tests. App tasks are verified by build, the Core suite, a startup smoke test, and the manual checklist in the task.

## File Structure

```
src/ReBackup.Core/
  Ignore/IgnorePattern.cs        one parsed gitignore line: parse, match, escape
  Ignore/IgnoreMatcher.cs        ordered pattern set, precedence, parent rule (+ NestedIgnoreFile, IgnoreResult, IgnoreOrigins)
  Plans/IgnoreSettings.cs        plan "ignore" section model
  Plans/BackupPlan.cs            (modify) add Ignore
  Plans/PlanStore.cs             (modify) null-safe ignore section after load
  Indexing/SourceIndexer.cs      scan source into IndexNode tree (+ IndexNode, SourceIndex, IndexProgress)
  Indexing/IndexEvaluator.cs     apply matcher to index (+ EvaluatedNode, IncludeStatus)
  Indexing/TreemapLayout.cs      squarified treemap (+ TreemapRect, TreemapTile)
  IO/ByteSize.cs                 "1.5 GB" formatting
src/ReBackup.App/
  ViewModels/PlanEditorViewModel.cs      (modify) ignore fields, Preview, context commands
  ViewModels/MainViewModel.cs            (modify) pass global defaults to editors
  ViewModels/RangeObservableCollection.cs
  ViewModels/PreviewRowViewModel.cs      one visible tree row
  ViewModels/PreviewTreeViewModel.cs     flattened expandable tree
  ViewModels/IgnorePreviewViewModel.cs   index / evaluate / summary / selection
  Views/IgnorePreviewView.xaml(.cs)      the tab content
  Controls/TreemapControl.cs             custom-drawn treemap
  MainWindow.xaml                        (modify) single TabControl, second tab
  App.xaml.cs                            (modify) pass settings to MainViewModel
tests/ReBackup.Core.Tests/
  Ignore/IgnorePatternTests.cs
  Ignore/IgnoreMatcherTests.cs
  Plans/PlanStoreTests.cs        (modify)
  Indexing/SourceIndexerTests.cs
  Indexing/IndexEvaluatorTests.cs
  Indexing/TreemapLayoutTests.cs
  IO/ByteSizeTests.cs
```

---

### Task 1: IgnorePattern

**Files:**
- Create: `src/ReBackup.Core/Ignore/IgnorePattern.cs`
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnorePatternTests.cs`

**Interfaces:**
- Produces:
  - `IgnorePattern.Parse(string line, string origin, string baseDirectory = "") → IgnorePattern?` (null for blank lines, comments and empty patterns)
  - `bool IsMatch(string relativePath, bool isDirectory)`: the path is relative to the source root, with forward slashes
  - properties `Text` (the trimmed line), `Origin`, `BaseDirectory`, `IsNegated`, `IsDirectoryOnly`, `IsAnchored`
  - `IgnorePattern.EscapeLiteral(string text) → string`: escapes glob characters so the text matches itself

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Ignore/IgnorePatternTests.cs`

```csharp
using FluentAssertions;
using ReBackup.Core.Ignore;

namespace ReBackup.Core.Tests.Ignore;

public class IgnorePatternTests
{
    [Theory]
    // simple names and extensions match at any depth
    [InlineData("*.tmp", "a.tmp", false, true)]
    [InlineData("*.tmp", "sub/deep/a.tmp", false, true)]
    [InlineData("*.tmp", "a.tmpx", false, false)]
    [InlineData("*.TMP", "a.tmp", false, true)]
    [InlineData("Thumbs.db", "pics/Thumbs.db", false, true)]
    [InlineData("Thumbs.db", "pics/Thumbs.dbx", false, false)]
    [InlineData("*", "anything", false, true)]
    // directory-only patterns
    [InlineData("node_modules/", "node_modules", true, true)]
    [InlineData("node_modules/", "node_modules", false, false)]
    [InlineData("node_modules/", "src/node_modules", true, true)]
    [InlineData("$RECYCLE.BIN/", "$RECYCLE.BIN", true, true)]
    [InlineData("System Volume Information/", "System Volume Information", true, true)]
    // anchoring
    [InlineData("/build", "build", true, true)]
    [InlineData("/build", "src/build", true, false)]
    [InlineData("docs/*.md", "docs/a.md", false, true)]
    [InlineData("docs/*.md", "docs/sub/a.md", false, false)]
    [InlineData("docs/*.md", "x/docs/a.md", false, false)]
    [InlineData("a*b", "a/b", false, false)]
    // double star
    [InlineData("**/cache", "cache", true, true)]
    [InlineData("**/cache", "a/b/cache", true, true)]
    [InlineData("logs/**", "logs/a/b.txt", false, true)]
    [InlineData("logs/**", "logs", true, false)]
    [InlineData("a/**/z", "a/z", false, true)]
    [InlineData("a/**/z", "a/b/c/z", false, true)]
    [InlineData("a/**/z", "a/b/zz", false, false)]
    // ? and character classes
    [InlineData("file?.txt", "file1.txt", false, true)]
    [InlineData("file?.txt", "file10.txt", false, false)]
    [InlineData("file[0-9].txt", "file5.txt", false, true)]
    [InlineData("file[!0-9].txt", "file5.txt", false, false)]
    [InlineData("file[!0-9].txt", "filex.txt", false, true)]
    // escapes
    [InlineData("\\#notes", "#notes", false, true)]
    [InlineData("\\!important", "!important", false, true)]
    [InlineData("a\\*b", "a*b", false, true)]
    [InlineData("a\\*b", "axb", false, false)]
    public void IsMatch_follows_gitignore_rules(string line, string path, bool isDirectory, bool expected)
    {
        var pattern = IgnorePattern.Parse(line, "Plan")!;

        pattern.IsMatch(path, isDirectory).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("# a comment")]
    [InlineData("!")]
    [InlineData("/")]
    public void Parse_returns_null_for_lines_without_a_pattern(string line)
    {
        IgnorePattern.Parse(line, "Plan").Should().BeNull();
    }

    [Fact]
    public void Parse_keeps_text_origin_and_flags()
    {
        var pattern = IgnorePattern.Parse("!/build/  \r", "Plan")!;

        pattern.Text.Should().Be("!/build/");
        pattern.Origin.Should().Be("Plan");
        pattern.IsNegated.Should().BeTrue();
        pattern.IsDirectoryOnly.Should().BeTrue();
        pattern.IsAnchored.Should().BeTrue();
        pattern.IsMatch("build", isDirectory: true).Should().BeTrue();
    }

    [Fact]
    public void Trailing_spaces_are_trimmed_unless_escaped()
    {
        IgnorePattern.Parse("foo   ", "Plan")!.IsMatch("foo", false).Should().BeTrue();
        IgnorePattern.Parse("foo\\ ", "Plan")!.IsMatch("foo ", false).Should().BeTrue();
        IgnorePattern.Parse("foo\\ ", "Plan")!.IsMatch("foo", false).Should().BeFalse();
    }

    [Fact]
    public void Base_directory_scopes_the_pattern()
    {
        var pattern = IgnorePattern.Parse("*.log", "sub/.backupignore", "sub")!;

        pattern.BaseDirectory.Should().Be("sub");
        pattern.IsMatch("sub/a.log", false).Should().BeTrue();
        pattern.IsMatch("sub/x/a.log", false).Should().BeTrue();
        pattern.IsMatch("a.log", false).Should().BeFalse();
        pattern.IsMatch("subx/a.log", false).Should().BeFalse();
    }

    [Fact]
    public void Anchored_pattern_is_relative_to_its_base_directory()
    {
        var pattern = IgnorePattern.Parse("/build", "sub/.backupignore", "sub")!;

        pattern.IsMatch("sub/build", true).Should().BeTrue();
        pattern.IsMatch("sub/x/build", true).Should().BeFalse();
        pattern.IsMatch("build", true).Should().BeFalse();
    }

    [Fact]
    public void Base_directory_is_normalised_to_forward_slashes()
    {
        IgnorePattern.Parse("*.log", "x", "a\\b\\")!.BaseDirectory.Should().Be("a/b");
    }

    [Fact]
    public void Invalid_character_class_falls_back_to_a_literal_match()
    {
        var pattern = IgnorePattern.Parse("x[z-a]", "Plan")!;

        pattern.IsMatch("x[z-a]", false).Should().BeTrue();
        pattern.IsMatch("xb", false).Should().BeFalse();
    }

    [Fact]
    public void EscapeLiteral_makes_a_name_match_itself()
    {
        const string name = "a[1]*?.txt";

        var escaped = IgnorePattern.EscapeLiteral(name);

        escaped.Should().Be("a\\[1\\]\\*\\?.txt");
        IgnorePattern.Parse("/" + escaped, "Plan")!.IsMatch(name, false).Should().BeTrue();
        IgnorePattern.Parse("/" + escaped, "Plan")!.IsMatch("a1xy.txt", false).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~IgnorePatternTests"`
Expected: build error `The type or namespace name 'Ignore' does not exist in the namespace 'ReBackup.Core'`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Ignore/IgnorePattern.cs`**

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace ReBackup.Core.Ignore;

/// <summary>One parsed gitignore-style line. Paths are relative to the source root and use forward slashes.</summary>
public sealed class IgnorePattern
{
    private const RegexOptions MatchOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly char[] GlobChars = ['*', '?', '[', '\\'];

    private readonly string _basePrefix;
    private readonly string? _literalName;
    private readonly string? _nameSuffix;
    private readonly Regex? _regex;

    private IgnorePattern(string text, string origin, string baseDirectory, bool negated, bool directoryOnly,
        bool anchored, string glob)
    {
        Text = text;
        Origin = origin;
        BaseDirectory = baseDirectory;
        IsNegated = negated;
        IsDirectoryOnly = directoryOnly;
        IsAnchored = anchored;
        _basePrefix = baseDirectory.Length == 0 ? "" : baseDirectory + "/";

        // Fast paths for the two most common shapes: "name" and "*.ext".
        if (!anchored && glob.IndexOfAny(GlobChars) < 0)
        {
            _literalName = glob;
        }
        else if (!anchored && glob.Length > 1 && glob[0] == '*' && glob.IndexOfAny(GlobChars, 1) < 0)
        {
            _nameSuffix = glob[1..];
        }
        else
        {
            var prefix = anchored ? "^" : "^(?:.*/)?";
            try
            {
                _regex = new Regex(prefix + GlobToRegex(glob) + "$", MatchOptions);
            }
            catch (ArgumentException)
            {
                // e.g. a reversed range like [z-a]: treat the whole pattern as literal text.
                _regex = new Regex(prefix + Regex.Escape(glob) + "$", MatchOptions);
            }
        }
    }

    /// <summary>The line as written, without trailing whitespace.</summary>
    public string Text { get; }

    /// <summary>Where the pattern comes from: "Global defaults", "Plan" or the path of a nested ignore file.</summary>
    public string Origin { get; }

    /// <summary>Folder the pattern is relative to ("" for the source root).</summary>
    public string BaseDirectory { get; }

    public bool IsNegated { get; }
    public bool IsDirectoryOnly { get; }
    public bool IsAnchored { get; }

    public static IgnorePattern? Parse(string line, string origin, string baseDirectory = "")
    {
        var text = TrimTrailingSpaces(line.TrimEnd('\r', '\n'));
        if (text.Length == 0 || text[0] == '#')
            return null;

        var glob = text;
        var negated = glob[0] == '!';
        if (negated)
            glob = glob[1..];

        var directoryOnly = glob.EndsWith('/');
        glob = glob.TrimEnd('/');
        if (glob.Length == 0)
            return null;

        var anchored = glob.Contains('/');
        glob = glob.TrimStart('/');
        if (glob.Length == 0)
            return null;

        return new IgnorePattern(text, origin, NormalizeDirectory(baseDirectory), negated, directoryOnly, anchored, glob);
    }

    public bool IsMatch(string relativePath, bool isDirectory)
    {
        if (IsDirectoryOnly && !isDirectory)
            return false;

        ReadOnlySpan<char> local = relativePath;
        if (_basePrefix.Length > 0)
        {
            if (!relativePath.StartsWith(_basePrefix, StringComparison.OrdinalIgnoreCase))
                return false;
            local = local[_basePrefix.Length..];
        }

        if (_literalName is not null)
            return NameOf(local).Equals(_literalName, StringComparison.OrdinalIgnoreCase);
        if (_nameSuffix is not null)
            return NameOf(local).EndsWith(_nameSuffix, StringComparison.OrdinalIgnoreCase);
        return _regex!.IsMatch(local);
    }

    /// <summary>Escapes glob characters so that the text only matches itself when used in a pattern.</summary>
    public static string EscapeLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '?' or '[' or ']')
                sb.Append('\\');
            sb.Append(c);
        }
        if (sb.Length > 0 && sb[^1] == ' ')
            sb.Insert(sb.Length - 1, '\\');
        return sb.ToString();
    }

    internal static string NormalizeDirectory(string directory) =>
        directory.Replace('\\', '/').Trim('/');

    private static ReadOnlySpan<char> NameOf(ReadOnlySpan<char> path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string TrimTrailingSpaces(string s)
    {
        var end = s.Length;
        while (end > 0 && s[end - 1] == ' ' && (end < 2 || s[end - 2] != '\\'))
            end--;
        return s[..end];
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    var atSegmentStart = i == 0 || glob[i - 1] == '/';
                    var after = i + 2;
                    if (atSegmentStart && after < glob.Length && glob[after] == '/')
                    {
                        sb.Append("(?:.*/)?");   // "**/" : zero or more folders
                        i = after + 1;
                        continue;
                    }
                    if (atSegmentStart && after == glob.Length)
                    {
                        sb.Append(".*");         // trailing "/**" : everything inside
                        i = after;
                        continue;
                    }
                    while (i < glob.Length && glob[i] == '*')
                        i++;
                    sb.Append("[^/]*");          // other runs of stars behave like one star
                    continue;
                }
                sb.Append("[^/]*");
                i++;
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else if (c == '[')
            {
                var next = AppendCharacterClass(glob, i, sb);
                if (next < 0)
                {
                    sb.Append("\\[");
                    i++;
                }
                else
                {
                    i = next;
                }
            }
            else if (c == '\\' && i + 1 < glob.Length)
            {
                sb.Append(Regex.Escape(glob[i + 1].ToString()));
                i += 2;
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Appends the regex for the class starting at <paramref name="start"/>; returns the index after it, or -1 if unterminated.</summary>
    private static int AppendCharacterClass(string glob, int start, StringBuilder sb)
    {
        var i = start + 1;
        var negate = i < glob.Length && (glob[i] == '!' || glob[i] == '^');
        if (negate)
            i++;
        var contentStart = i;
        if (i < glob.Length && glob[i] == ']')
            i++;   // a leading ] is a literal
        while (i < glob.Length && glob[i] != ']')
            i++;
        if (i >= glob.Length)
            return -1;

        sb.Append('[');
        if (negate)
            sb.Append('^');
        foreach (var ch in glob.AsSpan(contentStart, i - contentStart))
        {
            if (ch is '\\' or '[' or ']' or '^')
                sb.Append('\\');
            sb.Append(ch);
        }
        sb.Append(']');
        return i + 1;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~IgnorePatternTests"`
Expected: PASS (46 tests: 34 + 5 theory rows, 7 facts).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add gitignore-style IgnorePattern" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 2: Ignore section in the plan model

**Files:**
- Create: `src/ReBackup.Core/Plans/IgnoreSettings.cs`
- Modify: `src/ReBackup.Core/Plans/BackupPlan.cs`, `src/ReBackup.Core/Plans/PlanStore.cs` (inside `LoadAll`)
- Test: `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs` (modify)

**Interfaces:**
- Produces: `IgnoreSettings` with `bool UseGlobalDefaults` (default true), `bool HonorNestedFiles` (default true), `List<string> Patterns` (default empty); `BackupPlan.Ignore` (never null after `PlanStore.LoadAll`, `Patterns` never null).

- [ ] **Step 1: Write the failing tests**

In `tests/ReBackup.Core.Tests/Plans/PlanStoreTests.cs` add `using System.Text.Json;` at the top, **replace** the existing `Unknown_properties_survive_load_and_save` test with the stricter version below, and add the three new tests:

```csharp
    [Fact]
    public void Unknown_properties_survive_load_and_save()
    {
        using var store = NewStore();
        var path = store.PathFor("p1");
        File.WriteAllText(path, """
            { "id": "p1", "name": "Keep", "source": "C:\\x", "target": "D:\\y",
              "triggers": [ { "type": "Daily", "time": "02:00" } ],
              "retention": [ { "period": "Monthly", "anchor": 0, "keep": 12 } ] }
            """);

        var plan = store.LoadAll().Plans.Single();
        store.Save(plan);

        using var saved = JsonDocument.Parse(File.ReadAllText(path));
        var trigger = saved.RootElement.GetProperty("triggers")[0];
        trigger.GetProperty("type").GetString().Should().Be("Daily");
        trigger.GetProperty("time").GetString().Should().Be("02:00");
        var rule = saved.RootElement.GetProperty("retention")[0];
        rule.GetProperty("period").GetString().Should().Be("Monthly");
        rule.GetProperty("anchor").GetInt32().Should().Be(0);
        rule.GetProperty("keep").GetInt32().Should().Be(12);
    }

    [Fact]
    public void Ignore_section_round_trips_with_spec_property_names()
    {
        using var store = NewStore();
        var plan = new BackupPlan { Name = "Projects" };
        plan.Ignore.UseGlobalDefaults = false;
        plan.Ignore.HonorNestedFiles = false;
        plan.Ignore.Patterns.AddRange(["node_modules/", "*.tmp", "!keep.tmp"]);

        store.Save(plan);

        using var saved = JsonDocument.Parse(File.ReadAllText(store.PathFor(plan.Id)));
        var ignore = saved.RootElement.GetProperty("ignore");
        ignore.GetProperty("useGlobalDefaults").GetBoolean().Should().BeFalse();
        ignore.GetProperty("honorNestedFiles").GetBoolean().Should().BeFalse();
        ignore.GetProperty("patterns").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("node_modules/", "*.tmp", "!keep.tmp");
        store.LoadAll().Plans.Single().Ignore.Should().BeEquivalentTo(plan.Ignore);
    }

    [Fact]
    public void Missing_ignore_section_yields_defaults()
    {
        using var store = NewStore();
        File.WriteAllText(store.PathFor("p1"), """{ "id": "p1", "name": "Old" }""");

        var ignore = store.LoadAll().Plans.Single().Ignore;

        ignore.UseGlobalDefaults.Should().BeTrue();
        ignore.HonorNestedFiles.Should().BeTrue();
        ignore.Patterns.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{ "id": "p1", "name": "X", "ignore": null }""")]
    [InlineData("""{ "id": "p1", "name": "X", "ignore": { "patterns": null } }""")]
    public void Null_ignore_values_are_replaced_by_defaults(string json)
    {
        using var store = NewStore();
        File.WriteAllText(store.PathFor("p1"), json);

        var ignore = store.LoadAll().Plans.Single().Ignore;

        ignore.Should().NotBeNull();
        ignore.Patterns.Should().BeEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~PlanStoreTests"`
Expected: build error `'BackupPlan' does not contain a definition for 'Ignore'`.

- [ ] **Step 3: Create `src/ReBackup.Core/Plans/IgnoreSettings.cs`**

```csharp
namespace ReBackup.Core.Plans;

/// <summary>The "ignore" section of a plan.</summary>
public sealed class IgnoreSettings
{
    /// <summary>Also apply the default patterns from the global settings.</summary>
    public bool UseGlobalDefaults { get; set; } = true;

    /// <summary>Honor <c>.backupignore</c> files found inside the source tree.</summary>
    public bool HonorNestedFiles { get; set; } = true;

    /// <summary>gitignore-style lines, relative to the source root. Comments and blank lines are allowed.</summary>
    public List<string> Patterns { get; set; } = [];
}
```

- [ ] **Step 4: Add the property to `BackupPlan`**

In `src/ReBackup.Core/Plans/BackupPlan.cs`, add after the `FreeSpaceByRetention` property:

```csharp
    public IgnoreSettings Ignore { get; set; } = new();
```

And change the summary comment on `Extra` to:

```csharp
    /// <summary>Plan sections not yet modelled by this version (triggers, retention) survive a load/save round trip.</summary>
```

- [ ] **Step 5: Make `PlanStore.LoadAll` null-safe**

In `src/ReBackup.Core/Plans/PlanStore.cs`, inside `LoadAll`, directly before `plans.Add(plan);` add:

```csharp
                plan.Ignore ??= new IgnoreSettings();
                plan.Ignore.Patterns ??= [];
```

- [ ] **Step 6: Run all Core tests**

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: PASS (all tests, 0 build warnings).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): add ignore section to the plan model" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 3: IgnoreMatcher

**Files:**
- Create: `src/ReBackup.Core/Ignore/IgnoreMatcher.cs`
- Test: `tests/ReBackup.Core.Tests/Ignore/IgnoreMatcherTests.cs`

**Interfaces:**
- Consumes: `IgnorePattern.Parse`, `IgnorePattern.IsMatch`, `IgnoreSettings`
- Produces:
  - `record NestedIgnoreFile(string DirectoryRelativePath, IReadOnlyList<string> Lines)`
  - `readonly record struct IgnoreResult(bool IsIgnored, IgnorePattern? Pattern)`. `Pattern` is the deciding pattern, or null when nothing matched.
  - `IgnoreOrigins.GlobalDefaults` (`"Global defaults"`), `IgnoreOrigins.Plan` (`"Plan"`), `IgnoreOrigins.NestedFileName` (`".backupignore"`), `IgnoreOrigins.ForNestedFile(string directory)`
  - `IgnoreMatcher.Create(IEnumerable<string> globalDefaults, IEnumerable<string> planPatterns, IEnumerable<NestedIgnoreFile> nestedFiles)`
  - `IgnoreMatcher.ForPlan(IgnoreSettings settings, IEnumerable<string> globalDefaults, IEnumerable<NestedIgnoreFile> nestedFiles)`
  - `IgnoreResult MatchEntry(string relativePath, bool isDirectory)`: this entry alone, parents not considered
  - `IgnoreResult Match(string relativePath, bool isDirectory)`: includes the parent-excluded rule
  - `IReadOnlyList<IgnorePattern> Patterns`

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Ignore/IgnoreMatcherTests.cs`

```csharp
using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Plans;

namespace ReBackup.Core.Tests.Ignore;

public class IgnoreMatcherTests
{
    private static IgnoreMatcher Plan(params string[] patterns) => IgnoreMatcher.Create([], patterns, []);

    [Fact]
    public void Unmatched_path_is_not_ignored_and_has_no_pattern()
    {
        var result = Plan("*.tmp").Match("a.txt", false);

        result.IsIgnored.Should().BeFalse();
        result.Pattern.Should().BeNull();
    }

    [Fact]
    public void Last_matching_pattern_wins()
    {
        var matcher = Plan("*.log", "!keep.log");

        matcher.Match("a.log", false).IsIgnored.Should().BeTrue();
        var kept = matcher.Match("keep.log", false);
        kept.IsIgnored.Should().BeFalse();
        kept.Pattern!.Text.Should().Be("!keep.log");
    }

    [Fact]
    public void Order_of_patterns_matters()
    {
        Plan("!keep.log", "*.log").Match("keep.log", false).IsIgnored.Should().BeTrue();
    }

    [Fact]
    public void Comments_and_blank_lines_are_skipped()
    {
        Plan("# comment", "", "*.a").Patterns.Should().ContainSingle().Which.Text.Should().Be("*.a");
    }

    [Fact]
    public void Plan_overrides_global_defaults_and_nested_files_override_the_plan()
    {
        var globalOnly = IgnoreMatcher.Create(["*.tmp"], [], []);
        var withPlan = IgnoreMatcher.Create(["*.tmp"], ["!important.tmp"], []);
        var withNested = IgnoreMatcher.Create(["*.tmp"], ["!important.tmp"],
            [new NestedIgnoreFile("", ["important.tmp"])]);

        var byGlobal = globalOnly.Match("important.tmp", false);
        byGlobal.IsIgnored.Should().BeTrue();
        byGlobal.Pattern!.Origin.Should().Be("Global defaults");

        var byPlan = withPlan.Match("important.tmp", false);
        byPlan.IsIgnored.Should().BeFalse();
        byPlan.Pattern!.Origin.Should().Be("Plan");

        var byNested = withNested.Match("important.tmp", false);
        byNested.IsIgnored.Should().BeTrue();
        byNested.Pattern!.Origin.Should().Be(".backupignore");
    }

    [Fact]
    public void Nested_file_only_applies_below_its_folder()
    {
        var matcher = IgnoreMatcher.Create([], [], [new NestedIgnoreFile("sub", ["*.txt"])]);

        var inside = matcher.Match("sub/a.txt", false);
        inside.IsIgnored.Should().BeTrue();
        inside.Pattern!.Origin.Should().Be("sub/.backupignore");
        matcher.Match("a.txt", false).IsIgnored.Should().BeFalse();
    }

    [Fact]
    public void Deeper_nested_file_overrides_a_shallower_one_regardless_of_input_order()
    {
        var matcher = IgnoreMatcher.Create([], [],
        [
            new NestedIgnoreFile("sub\\deep", ["!*.txt"]),
            new NestedIgnoreFile("", ["*.txt"]),
        ]);

        matcher.Match("a.txt", false).IsIgnored.Should().BeTrue();
        matcher.Match("sub/a.txt", false).IsIgnored.Should().BeTrue();
        var reincluded = matcher.Match("sub/deep/a.txt", false);
        reincluded.IsIgnored.Should().BeFalse();
        reincluded.Pattern!.Origin.Should().Be("sub/deep/.backupignore");
    }

    [Fact]
    public void File_below_an_ignored_folder_cannot_be_reincluded()
    {
        var matcher = Plan("build/", "!build/keep.txt");

        matcher.MatchEntry("build/keep.txt", false).IsIgnored.Should().BeFalse();
        var full = matcher.Match("build/keep.txt", false);
        full.IsIgnored.Should().BeTrue();
        full.Pattern!.Text.Should().Be("build/");
        matcher.Match("a/build/x/y.txt", false).IsIgnored.Should().BeTrue();
    }

    [Fact]
    public void ForPlan_honours_the_two_switches()
    {
        var nested = new[] { new NestedIgnoreFile("", ["*.nested"]) };
        var settings = new IgnoreSettings { Patterns = ["*.plan"] };

        var all = IgnoreMatcher.ForPlan(settings, ["*.global"], nested);
        all.Match("a.global", false).IsIgnored.Should().BeTrue();
        all.Match("a.plan", false).IsIgnored.Should().BeTrue();
        all.Match("a.nested", false).IsIgnored.Should().BeTrue();

        settings.UseGlobalDefaults = false;
        settings.HonorNestedFiles = false;
        var planOnly = IgnoreMatcher.ForPlan(settings, ["*.global"], nested);
        planOnly.Match("a.global", false).IsIgnored.Should().BeFalse();
        planOnly.Match("a.plan", false).IsIgnored.Should().BeTrue();
        planOnly.Match("a.nested", false).IsIgnored.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~IgnoreMatcherTests"`
Expected: build error `The type or namespace name 'IgnoreMatcher' could not be found`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Ignore/IgnoreMatcher.cs`**

```csharp
using ReBackup.Core.Plans;

namespace ReBackup.Core.Ignore;

/// <summary>The lines of one <c>.backupignore</c> file and the folder (relative to the source root) it lives in.</summary>
public sealed record NestedIgnoreFile(string DirectoryRelativePath, IReadOnlyList<string> Lines);

/// <summary><see cref="Pattern"/> is the pattern that decided, or null when no pattern matched.</summary>
public readonly record struct IgnoreResult(bool IsIgnored, IgnorePattern? Pattern)
{
    public static readonly IgnoreResult NotMatched = new(false, null);
}

public static class IgnoreOrigins
{
    public const string GlobalDefaults = "Global defaults";
    public const string Plan = "Plan";
    public const string NestedFileName = ".backupignore";

    public static string ForNestedFile(string directoryRelativePath) =>
        directoryRelativePath.Length == 0 ? NestedFileName : directoryRelativePath + "/" + NestedFileName;
}

public sealed class IgnoreMatcher
{
    private readonly IgnorePattern[] _patterns;   // lowest precedence first

    public IgnoreMatcher(IEnumerable<IgnorePattern> patternsLowestPrecedenceFirst) =>
        _patterns = [.. patternsLowestPrecedenceFirst];

    public IReadOnlyList<IgnorePattern> Patterns => _patterns;

    public static IgnoreMatcher Create(IEnumerable<string> globalDefaults, IEnumerable<string> planPatterns,
        IEnumerable<NestedIgnoreFile> nestedFiles)
    {
        var patterns = new List<IgnorePattern>();
        AddLines(patterns, globalDefaults, IgnoreOrigins.GlobalDefaults, "");
        AddLines(patterns, planPatterns, IgnoreOrigins.Plan, "");

        // Ancestors before descendants, so that deeper files take precedence.
        var ordered = nestedFiles
            .Select(f => (Directory: IgnorePattern.NormalizeDirectory(f.DirectoryRelativePath), f.Lines))
            .OrderBy(f => Depth(f.Directory))
            .ThenBy(f => f.Directory, StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, lines) in ordered)
            AddLines(patterns, lines, IgnoreOrigins.ForNestedFile(directory), directory);

        return new IgnoreMatcher(patterns);
    }

    public static IgnoreMatcher ForPlan(IgnoreSettings settings, IEnumerable<string> globalDefaults,
        IEnumerable<NestedIgnoreFile> nestedFiles) =>
        Create(
            settings.UseGlobalDefaults ? globalDefaults : [],
            settings.Patterns,
            settings.HonorNestedFiles ? nestedFiles : []);

    /// <summary>Decision for this entry alone: the last matching pattern wins. Parent folders are not considered.</summary>
    public IgnoreResult MatchEntry(string relativePath, bool isDirectory)
    {
        for (var i = _patterns.Length - 1; i >= 0; i--)
        {
            var pattern = _patterns[i];
            if (pattern.IsMatch(relativePath, isDirectory))
                return new IgnoreResult(!pattern.IsNegated, pattern);
        }
        return IgnoreResult.NotMatched;
    }

    /// <summary>Full decision: an entry below an ignored folder is ignored and cannot be re-included.</summary>
    public IgnoreResult Match(string relativePath, bool isDirectory)
    {
        var slash = relativePath.IndexOf('/');
        while (slash >= 0)
        {
            var parent = MatchEntry(relativePath[..slash], isDirectory: true);
            if (parent.IsIgnored)
                return parent;
            slash = relativePath.IndexOf('/', slash + 1);
        }
        return MatchEntry(relativePath, isDirectory);
    }

    private static void AddLines(List<IgnorePattern> patterns, IEnumerable<string> lines, string origin, string baseDirectory)
    {
        foreach (var line in lines)
        {
            if (IgnorePattern.Parse(line, origin, baseDirectory) is { } pattern)
                patterns.Add(pattern);
        }
    }

    private static int Depth(string directory) =>
        directory.Length == 0 ? 0 : directory.Count(c => c == '/') + 1;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~IgnoreMatcherTests"`
Expected: PASS (9 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add IgnoreMatcher with precedence and nested ignore files" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 4: SourceIndexer

**Files:**
- Create: `src/ReBackup.Core/Indexing/SourceIndexer.cs`
- Test: `tests/ReBackup.Core.Tests/Indexing/SourceIndexerTests.cs`

**Interfaces:**
- Consumes: `PathUtil.Normalize`, `NestedIgnoreFile`, `IgnoreOrigins.NestedFileName`, test helper `TempDir` (`Root`, `PathOf`, `CreateDir`, `WriteFile`)
- Produces:
  - `IndexNode` with `Name`, `RelativePath` (`""` for the root), `IsDirectory`, `Size`, `LastWriteUtc`, `Children` (sorted by name, case-insensitive), `Error`
  - `record SourceIndex(string Root, IndexNode RootNode, IReadOnlyList<NestedIgnoreFile> IgnoreFiles, int FileCount, int DirectoryCount)`. `DirectoryCount` excludes the root.
  - `readonly record struct IndexProgress(int Files, int Directories, string CurrentDirectory)`
  - `SourceIndexer.Build(string root, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)` and `BuildAsync(...)` with the same parameters. They throw `DirectoryNotFoundException` when the root is missing and `OperationCanceledException` on cancel. Folder links (reparse points) are not followed and get `Error = "Link is not followed."`. An unreadable folder gets its exception message in `Error` and the scan continues.

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Indexing/SourceIndexerTests.cs`

```csharp
using FluentAssertions;
using ReBackup.Core.Indexing;
using ReBackup.Core.Tests.TestSupport;

namespace ReBackup.Core.Tests.Indexing;

public class SourceIndexerTests : IDisposable
{
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private string CreateSource()
    {
        _tmp.WriteFile(@"src\b.txt", "12345");
        _tmp.WriteFile(@"src\A.txt", "1");
        _tmp.WriteFile(@"src\sub\deep\c.bin", "1234567890");
        _tmp.CreateDir(@"src\empty");
        return _tmp.PathOf("src");
    }

    [Fact]
    public void Builds_a_tree_with_sizes_and_forward_slash_paths()
    {
        var index = SourceIndexer.Build(CreateSource());

        index.Root.Should().Be(_tmp.PathOf("src"));
        index.FileCount.Should().Be(3);
        index.DirectoryCount.Should().Be(3);

        var root = index.RootNode;
        root.Name.Should().Be("src");
        root.RelativePath.Should().Be("");
        root.IsDirectory.Should().BeTrue();
        root.Children.Select(c => c.Name).Should().Equal("A.txt", "b.txt", "empty", "sub");

        var b = root.Children.Single(c => c.Name == "b.txt");
        b.IsDirectory.Should().BeFalse();
        b.Size.Should().Be(5);
        b.RelativePath.Should().Be("b.txt");
        b.LastWriteUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        var c = root.Children.Single(n => n.Name == "sub").Children.Single().Children.Single();
        c.RelativePath.Should().Be("sub/deep/c.bin");
        c.Size.Should().Be(10);

        root.Children.Single(n => n.Name == "empty").Children.Should().BeEmpty();
    }

    [Fact]
    public void Collects_nested_ignore_files_with_their_folder()
    {
        var source = CreateSource();
        _tmp.WriteFile(@"src\.backupignore", "*.tmp\n# comment");
        _tmp.WriteFile(@"src\sub\deep\.BACKUPIGNORE", "!keep.tmp");

        var index = SourceIndexer.Build(source);

        index.IgnoreFiles.Should().HaveCount(2);
        index.IgnoreFiles.Single(f => f.DirectoryRelativePath == "").Lines.Should().Equal("*.tmp", "# comment");
        index.IgnoreFiles.Single(f => f.DirectoryRelativePath == "sub/deep").Lines.Should().Equal("!keep.tmp");
        index.RootNode.Children.Should().Contain(c => c.Name == ".backupignore", "ignore files are ordinary files");
    }

    [Fact]
    public void Missing_root_throws()
    {
        var act = () => SourceIndexer.Build(_tmp.PathOf("nope"));

        act.Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void Cancellation_stops_the_scan()
    {
        var source = CreateSource();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => SourceIndexer.Build(source, cancellationToken: cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Reports_final_progress()
    {
        var reports = new List<IndexProgress>();

        SourceIndexer.Build(CreateSource(), new SyncProgress(reports.Add));

        reports.Should().NotBeEmpty();
        reports[^1].Files.Should().Be(3);
        reports[^1].Directories.Should().Be(3);
    }

    [Fact]
    public async Task BuildAsync_returns_the_same_result()
    {
        var index = await SourceIndexer.BuildAsync(CreateSource());

        index.FileCount.Should().Be(3);
    }

    private sealed class SyncProgress(Action<IndexProgress> onReport) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => onReport(value);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~SourceIndexerTests"`
Expected: build error `The type or namespace name 'Indexing' does not exist in the namespace 'ReBackup.Core'`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Indexing/SourceIndexer.cs`**

```csharp
using System.Diagnostics;
using ReBackup.Core.Ignore;
using ReBackup.Core.IO;

namespace ReBackup.Core.Indexing;

public sealed class IndexNode
{
    public required string Name { get; init; }

    /// <summary>Path relative to the source root, with forward slashes; "" for the root.</summary>
    public required string RelativePath { get; init; }

    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime LastWriteUtc { get; init; }
    public IReadOnlyList<IndexNode> Children { get; init; } = [];

    /// <summary>Why this folder could not be (fully) read, if so.</summary>
    public string? Error { get; init; }
}

/// <summary>One scan of a source folder. <see cref="DirectoryCount"/> does not include the root.</summary>
public sealed record SourceIndex(
    string Root,
    IndexNode RootNode,
    IReadOnlyList<NestedIgnoreFile> IgnoreFiles,
    int FileCount,
    int DirectoryCount);

public readonly record struct IndexProgress(int Files, int Directories, string CurrentDirectory);

public static class SourceIndexer
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public static Task<SourceIndex> BuildAsync(string root, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(root, progress, cancellationToken), cancellationToken);

    public static SourceIndex Build(string root, IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullRoot = PathUtil.Normalize(root);
        if (!Directory.Exists(fullRoot))
            throw new DirectoryNotFoundException($"Source folder \"{fullRoot}\" does not exist.");

        var walk = new Walk(progress, cancellationToken);
        var name = Path.GetFileName(fullRoot);
        var rootNode = walk.ScanDirectory(new DirectoryInfo(fullRoot), name.Length > 0 ? name : fullRoot, "");
        progress?.Report(new IndexProgress(walk.Files, walk.Directories, fullRoot));
        return new SourceIndex(fullRoot, rootNode, walk.IgnoreFiles, walk.Files, walk.Directories);
    }

    private sealed class Walk(IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();

        public List<NestedIgnoreFile> IgnoreFiles { get; } = [];
        public int Files { get; private set; }
        public int Directories { get; private set; }

        public IndexNode ScanDirectory(DirectoryInfo directory, string name, string relativePath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportThrottled(directory.FullName);

            var children = new List<IndexNode>();
            string? error = null;
            try
            {
                foreach (var entry in directory.EnumerateFileSystemInfos())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var childPath = relativePath.Length == 0 ? entry.Name : relativePath + "/" + entry.Name;

                    if (entry is DirectoryInfo subdirectory)
                    {
                        Directories++;
                        if (subdirectory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        {
                            children.Add(new IndexNode
                            {
                                Name = subdirectory.Name,
                                RelativePath = childPath,
                                IsDirectory = true,
                                LastWriteUtc = subdirectory.LastWriteTimeUtc,
                                Error = "Link is not followed.",
                            });
                        }
                        else
                        {
                            children.Add(ScanDirectory(subdirectory, subdirectory.Name, childPath));
                        }
                    }
                    else if (entry is FileInfo file)
                    {
                        Files++;
                        children.Add(new IndexNode
                        {
                            Name = file.Name,
                            RelativePath = childPath,
                            IsDirectory = false,
                            Size = file.Length,
                            LastWriteUtc = file.LastWriteTimeUtc,
                        });
                        if (file.Name.Equals(IgnoreOrigins.NestedFileName, StringComparison.OrdinalIgnoreCase))
                            ReadIgnoreFile(file, relativePath);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                error = ex.Message;
            }

            children.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return new IndexNode
            {
                Name = name,
                RelativePath = relativePath,
                IsDirectory = true,
                LastWriteUtc = directory.LastWriteTimeUtc,
                Children = children,
                Error = error,
            };
        }

        private void ReadIgnoreFile(FileInfo file, string directoryRelativePath)
        {
            try
            {
                IgnoreFiles.Add(new NestedIgnoreFile(directoryRelativePath, File.ReadAllLines(file.FullName)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // An unreadable ignore file is treated as if it had no patterns.
            }
        }

        private void ReportThrottled(string currentDirectory)
        {
            if (progress is null || _sinceReport.Elapsed < ProgressInterval)
                return;
            _sinceReport.Restart();
            progress.Report(new IndexProgress(Files, Directories, currentDirectory));
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~SourceIndexerTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add SourceIndexer" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 5: IndexEvaluator and ByteSize

**Files:**
- Create: `src/ReBackup.Core/Indexing/IndexEvaluator.cs`, `src/ReBackup.Core/IO/ByteSize.cs`
- Test: `tests/ReBackup.Core.Tests/Indexing/IndexEvaluatorTests.cs`, `tests/ReBackup.Core.Tests/IO/ByteSizeTests.cs`

**Interfaces:**
- Consumes: `SourceIndex`, `IndexNode`, `IgnoreMatcher.MatchEntry`, `IgnorePattern`
- Produces:
  - `enum IncludeStatus { Included, Ignored, Partial }`
  - `EvaluatedNode` with `Node`, `Status`, `Pattern` (the deciding pattern, also set on entries ignored through a parent), `IgnoredByParent`, `IncludedSize`, `IgnoredSize`, `IncludedFiles`, `IgnoredFiles`, `TotalSize`, `TotalFiles`, `Children` (same order as the index)
  - `IndexEvaluator.Evaluate(SourceIndex index, IgnoreMatcher matcher, CancellationToken cancellationToken = default) → EvaluatedNode`. The root itself is never ignored.
  - `ByteSize.Format(long bytes) → string` (`"0 B"`, `"1023 B"`, `"1.5 KB"`, `"5.0 GB"`; invariant culture, base 1024)

- [ ] **Step 1: Write the failing tests**

`tests/ReBackup.Core.Tests/IO/ByteSizeTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.IO;

namespace ReBackup.Core.Tests.IO;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    [InlineData(5368709120L, "5.0 GB")]
    [InlineData(1099511627776L, "1.0 TB")]
    public void Format_uses_base_1024_with_one_decimal(long bytes, string expected)
    {
        ByteSize.Format(bytes).Should().Be(expected);
    }
}
```

`tests/ReBackup.Core.Tests/Indexing/IndexEvaluatorTests.cs`:

```csharp
using FluentAssertions;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;

namespace ReBackup.Core.Tests.Indexing;

public class IndexEvaluatorTests
{
    private static IndexNode File(string path, long size) => new()
    {
        Name = path[(path.LastIndexOf('/') + 1)..], RelativePath = path, IsDirectory = false, Size = size,
    };

    private static IndexNode Dir(string path, params IndexNode[] children) => new()
    {
        Name = path.Length == 0 ? "root" : path[(path.LastIndexOf('/') + 1)..],
        RelativePath = path, IsDirectory = true, Children = children,
    };

    // root: a.txt(10) b.tmp(5) build{ out.bin(100) keep.txt(1) } src{ x.cs(20) y.tmp(2) }
    private static SourceIndex SampleIndex() => new(
        @"C:\src",
        Dir("",
            File("a.txt", 10),
            File("b.tmp", 5),
            Dir("build", File("build/out.bin", 100), File("build/keep.txt", 1)),
            Dir("src", File("src/x.cs", 20), File("src/y.tmp", 2))),
        [], 6, 2);

    private static EvaluatedNode Child(EvaluatedNode node, string name) =>
        node.Children.Single(c => c.Node.Name == name);

    [Fact]
    public void Everything_is_included_without_patterns()
    {
        var root = IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], [], []));

        root.Status.Should().Be(IncludeStatus.Included);
        root.IncludedSize.Should().Be(138);
        root.IncludedFiles.Should().Be(6);
        root.IgnoredSize.Should().Be(0);
        root.IgnoredFiles.Should().Be(0);
        root.TotalSize.Should().Be(138);
        root.TotalFiles.Should().Be(6);
    }

    [Fact]
    public void Rolls_up_sizes_and_marks_partial_folders()
    {
        var matcher = IgnoreMatcher.Create([], ["*.tmp", "build/", "!build/keep.txt"], []);

        var root = IndexEvaluator.Evaluate(SampleIndex(), matcher);

        root.Status.Should().Be(IncludeStatus.Partial);
        root.IncludedSize.Should().Be(30);
        root.IncludedFiles.Should().Be(2);
        root.IgnoredSize.Should().Be(108);
        root.IgnoredFiles.Should().Be(4);

        var src = Child(root, "src");
        src.Status.Should().Be(IncludeStatus.Partial);
        Child(src, "x.cs").Status.Should().Be(IncludeStatus.Included);
        var yTmp = Child(src, "y.tmp");
        yTmp.Status.Should().Be(IncludeStatus.Ignored);
        yTmp.Pattern!.Text.Should().Be("*.tmp");
        yTmp.IgnoredByParent.Should().BeFalse();
    }

    [Fact]
    public void Entries_below_an_ignored_folder_are_ignored_by_parent()
    {
        var matcher = IgnoreMatcher.Create([], ["build/", "!build/keep.txt"], []);

        var build = Child(IndexEvaluator.Evaluate(SampleIndex(), matcher), "build");

        build.Status.Should().Be(IncludeStatus.Ignored);
        build.IgnoredByParent.Should().BeFalse();
        build.Pattern!.Text.Should().Be("build/");
        build.IgnoredSize.Should().Be(101);
        build.IgnoredFiles.Should().Be(2);

        var keep = Child(build, "keep.txt");
        keep.Status.Should().Be(IncludeStatus.Ignored);
        keep.IgnoredByParent.Should().BeTrue();
        keep.Pattern!.Text.Should().Be("build/");
    }

    [Fact]
    public void Reincluded_file_carries_the_negated_pattern()
    {
        var matcher = IgnoreMatcher.Create([], ["*.tmp", "!b.tmp"], []);

        var b = Child(IndexEvaluator.Evaluate(SampleIndex(), matcher), "b.tmp");

        b.Status.Should().Be(IncludeStatus.Included);
        b.Pattern!.Text.Should().Be("!b.tmp");
    }

    [Fact]
    public void Root_itself_is_never_ignored()
    {
        var root = IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], ["*"], []));

        root.Status.Should().Be(IncludeStatus.Partial);
        root.IncludedFiles.Should().Be(0);
        root.IgnoredFiles.Should().Be(6);
    }

    [Fact]
    public void Children_keep_the_index_order()
    {
        var root = IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], [], []));

        root.Children.Select(c => c.Node.Name).Should().Equal("a.txt", "b.tmp", "build", "src");
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => IndexEvaluator.Evaluate(SampleIndex(), IgnoreMatcher.Create([], [], []), cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~IndexEvaluatorTests|FullyQualifiedName~ByteSizeTests"`
Expected: build errors `The name 'IndexEvaluator' does not exist` / `The name 'ByteSize' does not exist`.

- [ ] **Step 3: Implement `src/ReBackup.Core/IO/ByteSize.cs`**

```csharp
using System.Globalization;

namespace ReBackup.Core.IO;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {Units[unit]}");
    }
}
```

- [ ] **Step 4: Implement `src/ReBackup.Core/Indexing/IndexEvaluator.cs`**

```csharp
using ReBackup.Core.Ignore;

namespace ReBackup.Core.Indexing;

public enum IncludeStatus
{
    /// <summary>The entry and everything below it is backed up.</summary>
    Included,
    /// <summary>The entry and everything below it is skipped.</summary>
    Ignored,
    /// <summary>A folder that is backed up, but something below it is skipped.</summary>
    Partial,
}

public sealed class EvaluatedNode
{
    public required IndexNode Node { get; init; }
    public required IncludeStatus Status { get; init; }

    /// <summary>The pattern that decided: for ignored entries the excluding pattern (of the entry or of its ignored parent), for re-included entries the negated pattern.</summary>
    public IgnorePattern? Pattern { get; init; }

    /// <summary>True when the entry is ignored only because a parent folder is ignored.</summary>
    public bool IgnoredByParent { get; init; }

    public long IncludedSize { get; init; }
    public long IgnoredSize { get; init; }
    public int IncludedFiles { get; init; }
    public int IgnoredFiles { get; init; }
    public IReadOnlyList<EvaluatedNode> Children { get; init; } = [];

    public long TotalSize => IncludedSize + IgnoredSize;
    public int TotalFiles => IncludedFiles + IgnoredFiles;
}

public static class IndexEvaluator
{
    /// <summary>Applies the matcher to a cached index. Nothing is read from disk.</summary>
    public static EvaluatedNode Evaluate(SourceIndex index, IgnoreMatcher matcher,
        CancellationToken cancellationToken = default) =>
        Evaluate(index.RootNode, matcher, isRoot: true, parentIgnored: false, parentPattern: null, cancellationToken);

    private static EvaluatedNode Evaluate(IndexNode node, IgnoreMatcher matcher, bool isRoot, bool parentIgnored,
        IgnorePattern? parentPattern, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ignored = parentIgnored;
        var pattern = parentPattern;
        if (!parentIgnored && !isRoot)
        {
            var result = matcher.MatchEntry(node.RelativePath, node.IsDirectory);
            ignored = result.IsIgnored;
            pattern = result.Pattern;
        }

        if (!node.IsDirectory)
        {
            return new EvaluatedNode
            {
                Node = node,
                Status = ignored ? IncludeStatus.Ignored : IncludeStatus.Included,
                Pattern = pattern,
                IgnoredByParent = parentIgnored,
                IncludedSize = ignored ? 0 : node.Size,
                IgnoredSize = ignored ? node.Size : 0,
                IncludedFiles = ignored ? 0 : 1,
                IgnoredFiles = ignored ? 1 : 0,
            };
        }

        var children = new List<EvaluatedNode>(node.Children.Count);
        long includedSize = 0, ignoredSize = 0;
        int includedFiles = 0, ignoredFiles = 0;
        var anythingSkipped = false;
        foreach (var child in node.Children)
        {
            var evaluated = Evaluate(child, matcher, isRoot: false, ignored, ignored ? pattern : null, cancellationToken);
            children.Add(evaluated);
            includedSize += evaluated.IncludedSize;
            ignoredSize += evaluated.IgnoredSize;
            includedFiles += evaluated.IncludedFiles;
            ignoredFiles += evaluated.IgnoredFiles;
            anythingSkipped |= evaluated.Status != IncludeStatus.Included;
        }

        return new EvaluatedNode
        {
            Node = node,
            Status = ignored ? IncludeStatus.Ignored : anythingSkipped ? IncludeStatus.Partial : IncludeStatus.Included,
            Pattern = pattern,
            IgnoredByParent = parentIgnored,
            IncludedSize = includedSize,
            IgnoredSize = ignoredSize,
            IncludedFiles = includedFiles,
            IgnoredFiles = ignoredFiles,
            Children = children,
        };
    }
}
```

- [ ] **Step 5: Run all Core tests**

Run: `dotnet test tests/ReBackup.Core.Tests`
Expected: PASS (all tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(core): add IndexEvaluator and ByteSize" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 6: TreemapLayout

**Files:**
- Create: `src/ReBackup.Core/Indexing/TreemapLayout.cs`
- Test: `tests/ReBackup.Core.Tests/Indexing/TreemapLayoutTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct TreemapRect(double X, double Y, double Width, double Height)` with `Area`
  - `readonly record struct TreemapTile<T>(T Item, TreemapRect Rect)`
  - `TreemapLayout.Squarify<T>(IEnumerable<T> items, Func<T, double> weight, TreemapRect bounds) → IReadOnlyList<TreemapTile<T>>`. Items with weight ≤ 0 are left out. Tiles are returned largest first. The result is empty when there is nothing to lay out or the bounds have no area.

- [ ] **Step 1: Write the failing tests** `tests/ReBackup.Core.Tests/Indexing/TreemapLayoutTests.cs`

```csharp
using FluentAssertions;
using ReBackup.Core.Indexing;

namespace ReBackup.Core.Tests.Indexing;

public class TreemapLayoutTests
{
    private const double Tolerance = 1e-9;

    private static IReadOnlyList<TreemapTile<double>> Layout(TreemapRect bounds, params double[] weights) =>
        TreemapLayout.Squarify(weights, w => w, bounds);

    [Fact]
    public void Single_item_fills_the_bounds()
    {
        var bounds = new TreemapRect(10, 20, 300, 200);

        Layout(bounds, 5).Should().ContainSingle().Which.Rect.Should().Be(bounds);
    }

    [Fact]
    public void Two_equal_items_split_a_wide_rectangle_into_squares()
    {
        var tiles = Layout(new TreemapRect(0, 0, 100, 50), 1, 1);

        tiles.Select(t => t.Rect).Should().BeEquivalentTo(
            [new TreemapRect(0, 0, 50, 50), new TreemapRect(50, 0, 50, 50)]);
    }

    [Fact]
    public void Classic_example_places_the_first_row_as_a_column_on_the_left()
    {
        // Bruls, Huizing, van Wijk: weights 6,6,4,3,2,2,1 in a 6x4 rectangle.
        var tiles = Layout(new TreemapRect(0, 0, 6, 4), 6, 6, 4, 3, 2, 2, 1);

        tiles.Should().HaveCount(7);
        AssertRect(tiles[0].Rect, 0, 0, 3, 2);
        AssertRect(tiles[1].Rect, 0, 2, 3, 2);
        tiles.Select(t => t.Item).Should().Equal(6, 6, 4, 3, 2, 2, 1);
    }

    [Fact]
    public void Areas_are_proportional_to_weights_and_fill_the_bounds()
    {
        var bounds = new TreemapRect(5, 7, 640, 360);
        var weights = new double[] { 50, 3, 120, 8, 8, 1, 33, 0.5 };

        var tiles = Layout(bounds, weights);

        var scale = bounds.Area / weights.Sum();
        foreach (var tile in tiles)
            tile.Rect.Area.Should().BeApproximately(tile.Item * scale, 1e-6);
        tiles.Sum(t => t.Rect.Area).Should().BeApproximately(bounds.Area, 1e-6);
    }

    [Fact]
    public void Tiles_stay_inside_the_bounds()
    {
        var bounds = new TreemapRect(5, 7, 640, 360);

        var tiles = Layout(bounds, Enumerable.Range(1, 200).Select(i => (double)(i * i % 97 + 1)).ToArray());

        foreach (var rect in tiles.Select(t => t.Rect))
        {
            rect.X.Should().BeGreaterThanOrEqualTo(bounds.X - Tolerance);
            rect.Y.Should().BeGreaterThanOrEqualTo(bounds.Y - Tolerance);
            (rect.X + rect.Width).Should().BeLessThanOrEqualTo(bounds.X + bounds.Width + 1e-6);
            (rect.Y + rect.Height).Should().BeLessThanOrEqualTo(bounds.Y + bounds.Height + 1e-6);
            rect.Width.Should().BeGreaterThanOrEqualTo(0);
            rect.Height.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public void Items_without_weight_are_left_out()
    {
        Layout(new TreemapRect(0, 0, 10, 10), 0, 4, -1).Should().ContainSingle().Which.Item.Should().Be(4);
    }

    [Fact]
    public void Nothing_to_lay_out_yields_an_empty_result()
    {
        Layout(new TreemapRect(0, 0, 10, 10)).Should().BeEmpty();
        Layout(new TreemapRect(0, 0, 10, 10), 0, 0).Should().BeEmpty();
        Layout(new TreemapRect(0, 0, 0, 10), 1, 2).Should().BeEmpty();
    }

    private static void AssertRect(TreemapRect rect, double x, double y, double width, double height)
    {
        rect.X.Should().BeApproximately(x, Tolerance);
        rect.Y.Should().BeApproximately(y, Tolerance);
        rect.Width.Should().BeApproximately(width, Tolerance);
        rect.Height.Should().BeApproximately(height, Tolerance);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~TreemapLayoutTests"`
Expected: build error `The name 'TreemapLayout' does not exist`.

- [ ] **Step 3: Implement `src/ReBackup.Core/Indexing/TreemapLayout.cs`**

```csharp
namespace ReBackup.Core.Indexing;

public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
}

public readonly record struct TreemapTile<T>(T Item, TreemapRect Rect);

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk).</summary>
public static class TreemapLayout
{
    public static IReadOnlyList<TreemapTile<T>> Squarify<T>(IEnumerable<T> items, Func<T, double> weight,
        TreemapRect bounds)
    {
        var tiles = new List<TreemapTile<T>>();
        var weighted = items
            .Select(item => (Item: item, Weight: weight(item)))
            .Where(x => x.Weight > 0)
            .OrderByDescending(x => x.Weight)
            .ToList();
        if (weighted.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return tiles;

        var scale = bounds.Area / weighted.Sum(x => x.Weight);
        var row = new List<(T Item, double Area)>();
        var free = bounds;
        foreach (var (item, itemWeight) in weighted)
        {
            var area = itemWeight * scale;
            var side = Math.Min(free.Width, free.Height);
            if (row.Count > 0 && WorstRatio(row, area, side) > WorstRatio(row, 0, side))
            {
                free = PlaceRow(row, free, tiles);
                row.Clear();
            }
            row.Add((item, area));
        }
        PlaceRow(row, free, tiles);
        return tiles;
    }

    /// <summary>Worst aspect ratio in the row if <paramref name="extraArea"/> were added (0 = the row as it is).</summary>
    private static double WorstRatio<T>(List<(T Item, double Area)> row, double extraArea, double side)
    {
        var sum = extraArea;
        var max = extraArea;
        var min = extraArea > 0 ? extraArea : double.MaxValue;
        foreach (var (_, area) in row)
        {
            sum += area;
            max = Math.Max(max, area);
            min = Math.Min(min, area);
        }
        var side2 = side * side;
        var sum2 = sum * sum;
        return Math.Max(side2 * max / sum2, sum2 / (side2 * min));
    }

    /// <summary>Places the row along the shorter side of the free rectangle and returns what is left.</summary>
    private static TreemapRect PlaceRow<T>(List<(T Item, double Area)> row, TreemapRect free,
        List<TreemapTile<T>> tiles)
    {
        var sum = row.Sum(r => r.Area);
        if (free.Width >= free.Height)
        {
            var width = free.Height > 0 ? Math.Min(sum / free.Height, free.Width) : 0;
            var y = free.Y;
            foreach (var (item, area) in row)
            {
                var height = width > 0 ? area / width : 0;
                tiles.Add(new TreemapTile<T>(item, new TreemapRect(free.X, y, width, height)));
                y += height;
            }
            return new TreemapRect(free.X + width, free.Y, Math.Max(0, free.Width - width), free.Height);
        }
        else
        {
            var height = free.Width > 0 ? Math.Min(sum / free.Width, free.Height) : 0;
            var x = free.X;
            foreach (var (item, area) in row)
            {
                var width = height > 0 ? area / height : 0;
                tiles.Add(new TreemapTile<T>(item, new TreemapRect(x, free.Y, width, height)));
                x += width;
            }
            return new TreemapRect(free.X, free.Y + height, free.Width, Math.Max(0, free.Height - height));
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/ReBackup.Core.Tests --filter "FullyQualifiedName~TreemapLayoutTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): add squarified TreemapLayout" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 7: Ignore editor in the App and a single TabControl

**Files:**
- Create: `src/ReBackup.App/Views/IgnorePreviewView.xaml`, `src/ReBackup.App/Views/IgnorePreviewView.xaml.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/MainWindow.xaml`

**Interfaces:**
- Consumes: `BackupPlan.Ignore`, `IgnoreSettings`
- Produces: on `PlanEditorViewModel`: `string IgnorePatternsText`, `bool UseGlobalIgnoreDefaults`, `bool HonorNestedIgnoreFiles`, `IgnoreSettings CurrentIgnoreSettings()` (a fresh snapshot built from the editor fields). Editing any of them marks the plan dirty. `ToPlan()` writes them into `plan.Ignore`.

This task also fixes a Phase 1 carry-forward: the tab control was re-created on every plan switch, so the selected tab jumped back. There is now one `TabControl` whose `DataContext` is the selected plan.

- [ ] **Step 1: Extend `PlanEditorViewModel`**

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`:

Add these observable fields after `_freeSpaceByRetention`:

```csharp
    [ObservableProperty] private string _ignorePatternsText = "";
    [ObservableProperty] private bool _useGlobalIgnoreDefaults;
    [ObservableProperty] private bool _honorNestedIgnoreFiles;
```

Add these handlers after `OnFreeSpaceByRetentionChanged`:

```csharp
    partial void OnIgnorePatternsTextChanged(string value) => Touch();
    partial void OnUseGlobalIgnoreDefaultsChanged(bool value) => Touch();
    partial void OnHonorNestedIgnoreFilesChanged(bool value) => Touch();
```

In `ToPlan()`, add before `return plan;`:

```csharp
        plan.Ignore = CurrentIgnoreSettings();
```

Add this method after `ToPlan()`:

```csharp
    /// <summary>A snapshot of the ignore section as currently edited. Trailing blank lines are dropped.</summary>
    public IgnoreSettings CurrentIgnoreSettings()
    {
        var lines = IgnorePatternsText.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return new IgnoreSettings
        {
            UseGlobalDefaults = UseGlobalIgnoreDefaults,
            HonorNestedFiles = HonorNestedIgnoreFiles,
            Patterns = lines,
        };
    }
```

In `LoadFrom`, add after `FreeSpaceByRetention = plan.FreeSpaceByRetention;`:

```csharp
            IgnorePatternsText = string.Join(Environment.NewLine, plan.Ignore.Patterns);
            UseGlobalIgnoreDefaults = plan.Ignore.UseGlobalDefaults;
            HonorNestedIgnoreFiles = plan.Ignore.HonorNestedFiles;
```

- [ ] **Step 2: Create `src/ReBackup.App/Views/IgnorePreviewView.xaml`**

```xml
<UserControl x:Class="ReBackup.App.Views.IgnorePreviewView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="300" MinWidth="200" />
            <ColumnDefinition Width="8" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <DockPanel Grid.Column="0">
            <TextBlock DockPanel.Dock="Top" Text="Ignore patterns (gitignore syntax, one per line)"
                       FontWeight="SemiBold" TextWrapping="Wrap" Margin="0,0,0,4" />
            <CheckBox DockPanel.Dock="Bottom" Margin="0,6,0,0"
                      Content="Honor .backupignore files inside the source"
                      IsChecked="{Binding HonorNestedIgnoreFiles}" />
            <CheckBox DockPanel.Dock="Bottom" Margin="0,8,0,0"
                      Content="Also apply the global default patterns"
                      IsChecked="{Binding UseGlobalIgnoreDefaults}" />
            <TextBox AcceptsReturn="True" FontFamily="Consolas"
                     VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                     Text="{Binding IgnorePatternsText, UpdateSourceTrigger=PropertyChanged}" />
        </DockPanel>

        <GridSplitter Grid.Column="1" Width="8" HorizontalAlignment="Stretch" Background="Transparent" />
    </Grid>
</UserControl>
```

- [ ] **Step 3: Create `src/ReBackup.App/Views/IgnorePreviewView.xaml.cs`**

```csharp
using System.Windows.Controls;

namespace ReBackup.App.Views;

public partial class IgnorePreviewView : UserControl
{
    public IgnorePreviewView()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 4: Replace the plan template in `MainWindow.xaml` with one TabControl**

In `src/ReBackup.App/MainWindow.xaml`:

1. Add the namespace to the `<Window>` element (replace the `xmlns:vm=...` line, which is no longer used):

```xml
        xmlns:views="clr-namespace:ReBackup.App.Views"
```

2. Delete the whole `<Window.Resources>…</Window.Resources>` block.

3. Replace `<ContentControl Content="{Binding SelectedPlan}" />` with:

```xml
                <TabControl DataContext="{Binding SelectedPlan}">
                    <TabControl.Style>
                        <Style TargetType="TabControl">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding}" Value="{x:Null}">
                                    <Setter Property="Visibility" Value="Collapsed" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TabControl.Style>

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

                    <TabItem Header="Ignore &amp; Preview">
                        <views:IgnorePreviewView />
                    </TabItem>
                </TabControl>
```

- [ ] **Step 5: Build, test, smoke-test**

Run: `dotnet build` (expected: 0 warnings, 0 errors), then `dotnet test tests/ReBackup.Core.Tests` (expected: all pass).
Smoke test: start `src/ReBackup.App/bin/Debug/net9.0-windows/ReBackup.App.exe`, confirm it is still running after 5 s, then stop it.

Manual checklist (for the user; do not click through it as an agent):
1. A plan shows two tabs. Switching to another plan keeps the "Ignore & Preview" tab selected.
2. Typing a pattern marks the plan with "•". Save writes `"ignore": { "useGlobalDefaults": …, "honorNestedFiles": …, "patterns": [ … ] }` into the plan JSON.
3. Revert restores the saved patterns and both checkboxes.
4. With no plan in the list, the tab area is empty.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(app): ignore pattern editor tab, single TabControl for all plans" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 8: Index, tree list, summary and context menu

**Files:**
- Create: `src/ReBackup.App/ViewModels/RangeObservableCollection.cs`, `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs`, `src/ReBackup.App/ViewModels/PreviewTreeViewModel.cs`, `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs`
- Modify: `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`, `src/ReBackup.App/ViewModels/MainViewModel.cs`, `src/ReBackup.App/App.xaml.cs`, `src/ReBackup.App/Views/IgnorePreviewView.xaml`, `src/ReBackup.App/Views/IgnorePreviewView.xaml.cs`

**Interfaces:**
- Consumes: `SourceIndexer.BuildAsync`, `IndexEvaluator.Evaluate`, `IgnoreMatcher.ForPlan`, `EvaluatedNode`, `IncludeStatus`, `ByteSize.Format`, `IgnorePattern.EscapeLiteral`, `AppSettings.DefaultIgnorePatterns`, `PlanEditorViewModel.CurrentIgnoreSettings()`
- Produces:
  - `RangeObservableCollection<T>` with `ReplaceAll`, `InsertRange`, `RemoveRange` (each raises one Reset)
  - `PreviewRowViewModel(PreviewTreeViewModel tree, EvaluatedNode node, long parentTotalSize, int depth, bool isExpanded)` with `Node`, `Depth`, `IsExpanded`, `IsExpandable`, `Name`, `Indent`, `SizeText`, `FilesText`, `PercentOfParent`, `PercentText`, `StatusText`, `StatusDetail`, `IsIgnored`
  - `PreviewTreeViewModel` with `Rows`, `SelectedRow`, `SetRoot(EvaluatedNode?)`, `Reveal(EvaluatedNode)`
  - `IgnorePreviewViewModel(Func<string> source, Func<IgnoreSettings> ignoreSettings, Func<IReadOnlyList<string>> globalDefaults)` with `Tree`, `Root`, `SelectedNode`, `IsIndexing`, `ProgressText`, `Error`, `Summary`, `IndexCommand`, `CancelIndexCommand`, `RequestReevaluate()`, `Invalidate()`
  - `PlanEditorViewModel` constructor gains a 4th parameter `Func<IReadOnlyList<string>> globalIgnoreDefaults`; new members `Preview`, `IgnoreSelectedCommand`, `IgnoreSelectedExtensionCommand`, `UnignoreSelectedCommand`
  - `MainViewModel` constructor gains a parameter: `MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, IDialogService dialogs, Action openSettings)`

The tree is a flat, virtualized `ListView`: only expanded rows exist in `Rows`, so a folder with tens of thousands of entries stays responsive, and scrolling a row into view is a plain `ScrollIntoView`.

- [ ] **Step 1: Create `src/ReBackup.App/ViewModels/RangeObservableCollection.cs`**

```csharp
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ReBackup.App.ViewModels;

/// <summary>ObservableCollection that can change many items with a single Reset notification.</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    private List<T> List => (List<T>)Items;

    public void ReplaceAll(IEnumerable<T> items)
    {
        List.Clear();
        List.AddRange(items);
        RaiseReset();
    }

    public void InsertRange(int index, IReadOnlyCollection<T> items)
    {
        if (items.Count == 0)
            return;
        List.InsertRange(index, items);
        RaiseReset();
    }

    public void RemoveRange(int index, int count)
    {
        if (count == 0)
            return;
        List.RemoveRange(index, count);
        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
```

- [ ] **Step 2: Create `src/ReBackup.App/ViewModels/PreviewRowViewModel.cs`**

```csharp
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;

namespace ReBackup.App.ViewModels;

/// <summary>One visible row of the preview tree.</summary>
public sealed class PreviewRowViewModel : ObservableObject
{
    private const double IndentPerLevel = 16;
    private readonly PreviewTreeViewModel _tree;
    private bool _isExpanded;

    public PreviewRowViewModel(PreviewTreeViewModel tree, EvaluatedNode node, long parentTotalSize, int depth,
        bool isExpanded)
    {
        _tree = tree;
        Node = node;
        Depth = depth;
        _isExpanded = isExpanded;
        PercentOfParent = parentTotalSize > 0 ? 100.0 * node.TotalSize / parentTotalSize : 0;
    }

    public EvaluatedNode Node { get; }
    public int Depth { get; }
    public double PercentOfParent { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                _tree.OnExpandedChanged(this, value);
        }
    }

    public bool IsExpandable => Node.Children.Count > 0;
    public string Name => Node.Node.Name;
    public Thickness Indent => new(Depth * IndentPerLevel, 0, 0, 0);
    public string SizeText => ByteSize.Format(Node.TotalSize);
    public string FilesText => Node.Node.IsDirectory ? Node.TotalFiles.ToString("N0", CultureInfo.CurrentCulture) : "";
    public string PercentText => PercentOfParent.ToString("0.0", CultureInfo.CurrentCulture) + " %";
    public string StatusText => Node.Status.ToString();
    public bool IsIgnored => Node.Status == IncludeStatus.Ignored;

    public string StatusDetail
    {
        get
        {
            var detail = Node.Status switch
            {
                IncludeStatus.Ignored when Node.IgnoredByParent =>
                    $"Ignored because a parent folder is ignored by \"{Node.Pattern?.Text}\" ({Node.Pattern?.Origin})",
                IncludeStatus.Ignored =>
                    $"Ignored by \"{Node.Pattern?.Text}\" ({Node.Pattern?.Origin})",
                IncludeStatus.Partial =>
                    $"Partly ignored: {ByteSize.Format(Node.IgnoredSize)} in {Node.IgnoredFiles:N0} files are skipped",
                _ when Node.Pattern is not null =>
                    $"Re-included by \"{Node.Pattern.Text}\" ({Node.Pattern.Origin})",
                _ => "Included",
            };
            return Node.Node.Error is null ? detail : $"{detail}\nCould not be read: {Node.Node.Error}";
        }
    }
}
```

- [ ] **Step 3: Create `src/ReBackup.App/ViewModels/PreviewTreeViewModel.cs`**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using ReBackup.Core.Indexing;

namespace ReBackup.App.ViewModels;

/// <summary>The preview tree flattened to its visible rows, so that a plain virtualized list can show it.</summary>
public sealed partial class PreviewTreeViewModel : ObservableObject
{
    private EvaluatedNode? _root;

    [ObservableProperty] private PreviewRowViewModel? _selectedRow;

    public RangeObservableCollection<PreviewRowViewModel> Rows { get; } = new();

    /// <summary>Shows a new evaluation. Expanded folders and the selection are kept by path.</summary>
    public void SetRoot(EvaluatedNode? root)
    {
        var expanded = Rows.Where(r => r.IsExpanded).Select(r => r.Node.Node.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPath = SelectedRow?.Node.Node.RelativePath;
        if (_root is null)
            expanded.Add("");   // first evaluation: open the root

        _root = root;
        var rows = new List<PreviewRowViewModel>();
        if (root is not null)
            AppendRows(rows, root, root.TotalSize, 0, expanded);
        Rows.ReplaceAll(rows);

        SelectedRow = selectedPath is null
            ? null
            : Rows.FirstOrDefault(r => r.Node.Node.RelativePath.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Expands the folders above the node and selects its row.</summary>
    public void Reveal(EvaluatedNode target)
    {
        if (_root is null || Rows.Count == 0)
            return;

        var current = _root;
        var row = Rows[0];
        var path = target.Node.RelativePath;
        if (path.Length > 0)
        {
            foreach (var part in path.Split('/'))
            {
                var child = current.Children.FirstOrDefault(c => c.Node.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                if (child is null)
                    return;
                row.IsExpanded = true;
                var childRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, child));
                if (childRow is null)
                    return;
                current = child;
                row = childRow;
            }
        }
        SelectedRow = row;
    }

    internal void OnExpandedChanged(PreviewRowViewModel row, bool expanded)
    {
        var index = Rows.IndexOf(row);
        if (index < 0)
            return;

        if (expanded)
        {
            var children = Sorted(row.Node)
                .Select(child => new PreviewRowViewModel(this, child, row.Node.TotalSize, row.Depth + 1, isExpanded: false))
                .ToList();
            Rows.InsertRange(index + 1, children);
        }
        else
        {
            var count = 0;
            while (index + 1 + count < Rows.Count && Rows[index + 1 + count].Depth > row.Depth)
                count++;
            Rows.RemoveRange(index + 1, count);
        }
    }

    private void AppendRows(List<PreviewRowViewModel> rows, EvaluatedNode node, long parentTotalSize, int depth,
        HashSet<string> expanded)
    {
        var isExpanded = node.Children.Count > 0 && expanded.Contains(node.Node.RelativePath);
        rows.Add(new PreviewRowViewModel(this, node, parentTotalSize, depth, isExpanded));
        if (!isExpanded)
            return;
        foreach (var child in Sorted(node))
            AppendRows(rows, child, node.TotalSize, depth + 1, expanded);
    }

    private static IEnumerable<EvaluatedNode> Sorted(EvaluatedNode node) =>
        node.Children
            .OrderByDescending(c => c.TotalSize)
            .ThenBy(c => c.Node.Name, StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Create `src/ReBackup.App/ViewModels/IgnorePreviewViewModel.cs`**

```csharp
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Ignore;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;
using ReBackup.Core.Plans;

namespace ReBackup.App.ViewModels;

/// <summary>Indexes a plan's source once and re-evaluates the ignore patterns against that cached index.</summary>
public sealed partial class IgnorePreviewViewModel : ObservableObject
{
    private const string NotIndexedText = "Not indexed yet.";
    private static readonly TimeSpan ReevaluateDelay = TimeSpan.FromMilliseconds(300);

    private readonly Func<string> _source;
    private readonly Func<IgnoreSettings> _ignoreSettings;
    private readonly Func<IReadOnlyList<string>> _globalDefaults;
    private SourceIndex? _index;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _evaluateCts;

    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private string _progressText = NotIndexedText;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private EvaluatedNode? _root;
    [ObservableProperty] private EvaluatedNode? _selectedNode;

    public IgnorePreviewViewModel(Func<string> source, Func<IgnoreSettings> ignoreSettings,
        Func<IReadOnlyList<string>> globalDefaults)
    {
        _source = source;
        _ignoreSettings = ignoreSettings;
        _globalDefaults = globalDefaults;
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreviewTreeViewModel.SelectedRow))
                SelectedNode = Tree.SelectedRow?.Node;
        };
    }

    public PreviewTreeViewModel Tree { get; } = new();

    partial void OnSelectedNodeChanged(EvaluatedNode? value)
    {
        // Selection coming from outside the tree (the treemap): show it in the tree.
        if (value is not null && !ReferenceEquals(Tree.SelectedRow?.Node, value))
            Tree.Reveal(value);
    }

    /// <summary>Drops the cached index, e.g. after the source folder changed.</summary>
    public void Invalidate()
    {
        _indexCts?.Cancel();
        _evaluateCts?.Cancel();
        _index = null;
        Root = null;
        Tree.SetRoot(null);
        Summary = "";
        Error = null;
        ProgressText = NotIndexedText;
    }

    /// <summary>Re-applies the patterns to the cached index after a short pause in typing. No rescan.</summary>
    public async void RequestReevaluate()
    {
        if (_index is null)
            return;

        _evaluateCts?.Cancel();
        var cts = _evaluateCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(ReevaluateDelay, cts.Token);
            await EvaluateAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task IndexAsync()
    {
        var source = _source();
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            Error = "The source folder does not exist.";
            return;
        }

        _indexCts?.Cancel();
        _evaluateCts?.Cancel();
        var cts = _indexCts = new CancellationTokenSource();
        IsIndexing = true;
        Error = null;
        var progress = new Progress<IndexProgress>(p =>
        {
            if (!cts.IsCancellationRequested && IsIndexing)
                ProgressText = $"{p.Files:N0} files, {p.Directories:N0} folders — {p.CurrentDirectory}";
        });

        try
        {
            var index = await SourceIndexer.BuildAsync(source, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            _index = index;
            await EvaluateAsync(cts.Token);
            IsIndexing = false;
            ProgressText = $"Indexed {index.FileCount:N0} files in {index.DirectoryCount:N0} folders.";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_indexCts, cts))
                ProgressText = _index is null ? "Indexing canceled." : "Indexing canceled; showing the previous index.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_indexCts, cts))
                IsIndexing = false;
        }
    }

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        if (_index is not { } index)
            return;

        // Snapshots, because the evaluation runs on a worker thread.
        var settings = _ignoreSettings();
        var globalDefaults = _globalDefaults().ToList();

        var root = await Task.Run(
            () => IndexEvaluator.Evaluate(index, IgnoreMatcher.ForPlan(settings, globalDefaults, index.IgnoreFiles), cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Root = root;
        Tree.SetRoot(root);
        Summary = $"Included: {root.IncludedFiles:N0} files, {ByteSize.Format(root.IncludedSize)}   ·   " +
                  $"Ignored: {root.IgnoredFiles:N0} files, {ByteSize.Format(root.IgnoredSize)}";
    }
}
```

- [ ] **Step 5: Wire the preview into `PlanEditorViewModel`**

In `src/ReBackup.App/ViewModels/PlanEditorViewModel.cs`:

Add usings:

```csharp
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReBackup.Core.Ignore;
```

Replace the constructor with (the new parameter, and `Preview` is created before `LoadFrom` because the change handlers use it):

```csharp
    public PlanEditorViewModel(BackupPlan plan, bool isNew, Func<IEnumerable<BackupPlan>> allPlans,
        Func<IReadOnlyList<string>> globalIgnoreDefaults)
    {
        _saved = plan.Clone();
        _allPlans = allPlans;
        Preview = new IgnorePreviewViewModel(() => Source, CurrentIgnoreSettings, globalIgnoreDefaults);
        IsNew = isNew;
        LoadFrom(_saved);
        IsDirty = isNew;
        Validate();
    }

    /// <summary>Index and preview of this plan's source; cached for the session.</summary>
    public IgnorePreviewViewModel Preview { get; }
```

Replace the `OnSourceChanged` handler and the three ignore handlers from Task 7 with:

```csharp
    partial void OnSourceChanged(string value)
    {
        Preview.Invalidate();
        Touch();
    }

    partial void OnIgnorePatternsTextChanged(string value)
    {
        Preview.RequestReevaluate();
        Touch();
    }

    partial void OnUseGlobalIgnoreDefaultsChanged(bool value)
    {
        Preview.RequestReevaluate();
        Touch();
    }

    partial void OnHonorNestedIgnoreFilesChanged(bool value)
    {
        Preview.RequestReevaluate();
        Touch();
    }
```

Add these commands after `CurrentIgnoreSettings()`:

```csharp
    [RelayCommand]
    private void IgnoreSelected()
    {
        if (SelectedEntry() is { } node)
            AppendPattern("/" + IgnorePattern.EscapeLiteral(node.RelativePath) + (node.IsDirectory ? "/" : ""));
    }

    [RelayCommand]
    private void IgnoreSelectedExtension()
    {
        if (SelectedEntry() is { IsDirectory: false } node && Path.GetExtension(node.Name) is { Length: > 1 } extension)
            AppendPattern("*" + IgnorePattern.EscapeLiteral(extension));
    }

    [RelayCommand]
    private void UnignoreSelected()
    {
        if (SelectedEntry() is { } node)
            AppendPattern("!/" + IgnorePattern.EscapeLiteral(node.RelativePath) + (node.IsDirectory ? "/" : ""));
    }

    /// <summary>The selected preview entry, unless it is the source root (which cannot be ignored).</summary>
    private ReBackup.Core.Indexing.IndexNode? SelectedEntry() =>
        Preview.SelectedNode?.Node is { RelativePath.Length: > 0 } node ? node : null;

    private void AppendPattern(string pattern)
    {
        var text = IgnorePatternsText.TrimEnd('\r', '\n');
        IgnorePatternsText = text.Length == 0 ? pattern : text + Environment.NewLine + pattern;
    }
```

- [ ] **Step 6: Pass the global defaults through `MainViewModel` and `App`**

In `src/ReBackup.App/ViewModels/MainViewModel.cs`:

Add `using ReBackup.Core.Settings;`, add the field `private readonly AppSettings _settings;`, and change the constructor signature and first lines to:

```csharp
    public MainViewModel(PlanStore store, ConfigPaths paths, AppSettings settings, IDialogService dialogs, Action openSettings)
    {
        _store = store;
        _paths = paths;
        _settings = settings;
        _dialogs = dialogs;
        _openSettings = openSettings;
```

Add this method next to `AllPlans()`:

```csharp
    // Read on every use, so edits made in the settings dialog apply to the next evaluation.
    private IReadOnlyList<string> GlobalIgnoreDefaults() => _settings.DefaultIgnorePatterns;
```

Change all three `new PlanEditorViewModel(...)` calls (constructor load loop, `ReloadFromDisk`, `NewPlan`) to pass the extra argument, e.g.:

```csharp
            AddEditor(new PlanEditorViewModel(plan, isNew: false, AllPlans, GlobalIgnoreDefaults));
```

```csharp
        var editor = new PlanEditorViewModel(new BackupPlan { Name = UniqueName("New plan") }, isNew: true, AllPlans, GlobalIgnoreDefaults);
```

In `src/ReBackup.App/App.xaml.cs`, in `LoadConfiguration`, change the view-model construction to:

```csharp
            var mainViewModel = new MainViewModel(planStore, paths, settings, _dialogs, ShowSettings);
```

- [ ] **Step 7: Replace `src/ReBackup.App/Views/IgnorePreviewView.xaml`**

```xml
<UserControl x:Class="ReBackup.App.Views.IgnorePreviewView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="12">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="300" MinWidth="200" />
            <ColumnDefinition Width="8" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <DockPanel Grid.Column="0">
            <TextBlock DockPanel.Dock="Top" Text="Ignore patterns (gitignore syntax, one per line)"
                       FontWeight="SemiBold" TextWrapping="Wrap" Margin="0,0,0,4" />
            <CheckBox DockPanel.Dock="Bottom" Margin="0,6,0,0"
                      Content="Honor .backupignore files inside the source"
                      IsChecked="{Binding HonorNestedIgnoreFiles}" />
            <CheckBox DockPanel.Dock="Bottom" Margin="0,8,0,0"
                      Content="Also apply the global default patterns"
                      IsChecked="{Binding UseGlobalIgnoreDefaults}" />
            <TextBox AcceptsReturn="True" FontFamily="Consolas"
                     VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto"
                     Text="{Binding IgnorePatternsText, UpdateSourceTrigger=PropertyChanged}" />
        </DockPanel>

        <GridSplitter Grid.Column="1" Width="8" HorizontalAlignment="Stretch" Background="Transparent" />

        <DockPanel Grid.Column="2">
            <DockPanel DockPanel.Dock="Top" Margin="0,0,0,6">
                <Button DockPanel.Dock="Left" Content="Index now" Padding="10,2"
                        Command="{Binding Preview.IndexCommand}" />
                <Button DockPanel.Dock="Left" Content="Cancel" Padding="10,2" Margin="6,0,0,0"
                        Command="{Binding Preview.CancelIndexCommand}"
                        IsEnabled="{Binding Preview.IsIndexing}" />
                <ProgressBar DockPanel.Dock="Left" Width="80" Height="12" Margin="8,0,0,0"
                             IsIndeterminate="{Binding Preview.IsIndexing}" />
                <TextBlock Margin="8,0,0,0" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"
                           Text="{Binding Preview.ProgressText}" />
            </DockPanel>

            <TextBlock DockPanel.Dock="Top" Foreground="Firebrick" TextWrapping="Wrap"
                       Text="{Binding Preview.Error}">
                <TextBlock.Style>
                    <Style TargetType="TextBlock">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Preview.Error}" Value="{x:Null}">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>

            <TextBlock DockPanel.Dock="Bottom" Margin="0,6,0,0" FontWeight="SemiBold"
                       Text="{Binding Preview.Summary}" />

            <Grid x:Name="PreviewArea">
                <Grid.RowDefinitions>
                    <RowDefinition Height="*" MinHeight="120" />
                </Grid.RowDefinitions>

                <ListView x:Name="RowsList" Grid.Row="0" SelectionMode="Single"
                          ItemsSource="{Binding Preview.Tree.Rows}"
                          SelectedItem="{Binding Preview.Tree.SelectedRow}"
                          SelectionChanged="OnRowSelectionChanged"
                          VirtualizingPanel.IsVirtualizing="True"
                          VirtualizingPanel.VirtualizationMode="Recycling">
                    <ListView.ContextMenu>
                        <ContextMenu>
                            <MenuItem Header="Ignore this" Command="{Binding IgnoreSelectedCommand}" />
                            <MenuItem Header="Ignore all files with this extension"
                                      Command="{Binding IgnoreSelectedExtensionCommand}" />
                            <MenuItem Header="Un-ignore this" Command="{Binding UnignoreSelectedCommand}" />
                        </ContextMenu>
                    </ListView.ContextMenu>
                    <ListView.ItemContainerStyle>
                        <Style TargetType="ListViewItem">
                            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
                            <Setter Property="ToolTip" Value="{Binding StatusDetail}" />
                        </Style>
                    </ListView.ItemContainerStyle>
                    <ListView.View>
                        <GridView>
                            <GridViewColumn Header="Name" Width="300">
                                <GridViewColumn.CellTemplate>
                                    <DataTemplate>
                                        <StackPanel Orientation="Horizontal" Margin="{Binding Indent}">
                                            <ToggleButton Width="16" Height="16" Padding="0" Margin="0,0,4,0"
                                                          FontSize="9" IsChecked="{Binding IsExpanded}">
                                                <ToggleButton.Style>
                                                    <Style TargetType="ToggleButton">
                                                        <Setter Property="Content" Value="+" />
                                                        <Style.Triggers>
                                                            <Trigger Property="IsChecked" Value="True">
                                                                <Setter Property="Content" Value="−" />
                                                            </Trigger>
                                                            <DataTrigger Binding="{Binding IsExpandable}" Value="False">
                                                                <Setter Property="Visibility" Value="Hidden" />
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </ToggleButton.Style>
                                            </ToggleButton>
                                            <TextBlock Text="{Binding Name}">
                                                <TextBlock.Style>
                                                    <Style TargetType="TextBlock">
                                                        <Style.Triggers>
                                                            <DataTrigger Binding="{Binding IsIgnored}" Value="True">
                                                                <Setter Property="Foreground" Value="Gray" />
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </TextBlock.Style>
                                            </TextBlock>
                                        </StackPanel>
                                    </DataTemplate>
                                </GridViewColumn.CellTemplate>
                            </GridViewColumn>
                            <GridViewColumn Header="Size" Width="80" DisplayMemberBinding="{Binding SizeText}" />
                            <GridViewColumn Header="Files" Width="70" DisplayMemberBinding="{Binding FilesText}" />
                            <GridViewColumn Header="% of parent" Width="130">
                                <GridViewColumn.CellTemplate>
                                    <DataTemplate>
                                        <Grid>
                                            <ProgressBar Height="14" Maximum="100"
                                                         Value="{Binding PercentOfParent, Mode=OneWay}" />
                                            <TextBlock HorizontalAlignment="Center" FontSize="11"
                                                       Text="{Binding PercentText}" />
                                        </Grid>
                                    </DataTemplate>
                                </GridViewColumn.CellTemplate>
                            </GridViewColumn>
                            <GridViewColumn Header="Status" Width="80" DisplayMemberBinding="{Binding StatusText}" />
                        </GridView>
                    </ListView.View>
                </ListView>
            </Grid>
        </DockPanel>
    </Grid>
</UserControl>
```

- [ ] **Step 8: Replace `src/ReBackup.App/Views/IgnorePreviewView.xaml.cs`**

```csharp
using System.Windows.Controls;

namespace ReBackup.App.Views;

public partial class IgnorePreviewView : UserControl
{
    public IgnorePreviewView()
    {
        InitializeComponent();
    }

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RowsList.SelectedItem is { } row)
            RowsList.ScrollIntoView(row);
    }
}
```

- [ ] **Step 9: Build, test, smoke-test**

Run: `dotnet build` (expected: 0 warnings, 0 errors), then `dotnet test tests/ReBackup.Core.Tests` (expected: all pass).
Smoke test: start the built exe, confirm it is still running after 5 s, then stop it.

Manual checklist (for the user; do not click through it as an agent):
1. **Index now** on a plan with a real source shows progress, then a tree with the root expanded, sorted by size, with size, file count, "% of parent" bars and status.
2. **Cancel** during a long index stops it and says so.
3. Typing `*.tmp` (or any pattern that matches) updates statuses and the summary about a third of a second after the last keystroke, without re-indexing.
4. Hovering an ignored row names the pattern and where it comes from (Plan, Global defaults, or a `.backupignore` path). A file below an ignored folder says the parent is ignored.
5. Right-click a row: **Ignore this** appends `/path` (or `/path/` for a folder), **Ignore all files with this extension** appends `*.ext`, **Un-ignore this** appends `!/path`. Each marks the plan dirty and updates the tree.
6. Unchecking "Also apply the global default patterns" re-evaluates. A `.backupignore` file in the source is honoured, and unchecking the second box stops that.
7. Expanded folders and the selection survive a re-evaluation. Changing the Source on the General tab clears the preview.
8. Switching to another plan and back keeps each plan's index.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(app): source index preview with live ignore evaluation" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

### Task 9: Treemap

**Files:**
- Create: `src/ReBackup.App/Controls/TreemapControl.cs`
- Modify: `src/ReBackup.App/Views/IgnorePreviewView.xaml`, `docs/superpowers/specs/2026-09-30-rebackup-design.md` (§10.2 tab 3, the treemap bullet)

**Interfaces:**
- Consumes: `TreemapLayout.Squarify`, `TreemapRect`, `EvaluatedNode`, `IncludeStatus`, `ByteSize.Format`, `IgnorePreviewViewModel.Root`, `IgnorePreviewViewModel.SelectedNode`
- Produces: `TreemapControl` (a `FrameworkElement`) with dependency properties `Root` (`EvaluatedNode?`) and `Selected` (`EvaluatedNode?`, binds two-way by default).

The treemap shows the whole source, as WinDirStat does, and highlights the selected node. Clicking a rectangle selects that entry, and the tree reveals it (through `IgnorePreviewViewModel.SelectedNode`, already wired in Task 8). The spec said "treemap of the selected tree node", which would re-root the map on every click, so the spec line is updated in this task.

- [ ] **Step 1: Create `src/ReBackup.App/Controls/TreemapControl.cs`**

```csharp
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ReBackup.Core.Indexing;
using ReBackup.Core.IO;

namespace ReBackup.App.Controls;

/// <summary>Draws an evaluated source tree as a squarified treemap. Ignored entries are grey.</summary>
public sealed class TreemapControl : FrameworkElement
{
    private const double MinTile = 3;    // smaller rectangles are not drawn
    private const double MinSplit = 8;   // folders smaller than this are drawn as one tile

    private static readonly Brush IgnoredBrush = Frozen(0xC8, 0xC8, 0xC8);
    private static readonly Brush FolderBrush = Frozen(0x5A, 0x6B, 0x7D);
    private static readonly Brush[] Palette =
    [
        Frozen(0x4E, 0x79, 0xA7), Frozen(0xF2, 0x8E, 0x2B), Frozen(0x59, 0xA1, 0x4F), Frozen(0xE1, 0x57, 0x59),
        Frozen(0x76, 0xB7, 0xB2), Frozen(0xED, 0xC9, 0x48), Frozen(0xB0, 0x7A, 0xA1), Frozen(0x9C, 0x75, 0x5F),
    ];
    private static readonly Pen TilePen = FrozenPen(Colors.White, 0.5);
    private static readonly Pen SelectionPen = FrozenPen(Colors.Black, 2);

    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(EvaluatedNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(EvaluatedNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private readonly List<(EvaluatedNode Node, Rect Rect)> _leafTiles = [];
    private readonly Dictionary<EvaluatedNode, Rect> _rects = [];

    public EvaluatedNode? Root
    {
        get => (EvaluatedNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public EvaluatedNode? Selected
    {
        get => (EvaluatedNode?)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        _leafTiles.Clear();
        _rects.Clear();

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRectangle(Brushes.Transparent, null, bounds);   // keeps the whole area hit-testable
        if (Root is not { TotalSize: > 0 } root || bounds.Width < MinTile || bounds.Height < MinTile)
            return;

        Draw(drawingContext, root, bounds);

        if (Selected is { } selected && _rects.TryGetValue(selected, out var rect))
            drawingContext.DrawRectangle(null, SelectionPen, rect);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (TileAt(e.GetPosition(this)) is { } node)
            SetCurrentValue(SelectedProperty, node);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = TileAt(e.GetPosition(this));
        var tip = node is null
            ? null
            : $"{node.Node.RelativePath}\n{ByteSize.Format(node.TotalSize)} — {node.Status}";
        if (!Equals(ToolTip, tip))
            ToolTip = tip;
    }

    private void Draw(DrawingContext dc, EvaluatedNode node, Rect rect)
    {
        if (rect.Width < MinTile || rect.Height < MinTile)
            return;
        _rects[node] = rect;

        var split = node.Node.IsDirectory && node.Children.Count > 0 && node.TotalSize > 0
                    && rect.Width >= MinSplit && rect.Height >= MinSplit;
        if (!split)
        {
            dc.DrawRectangle(BrushFor(node), TilePen, rect);
            _leafTiles.Add((node, rect));
            return;
        }

        dc.DrawRectangle(FolderBrush, null, rect);   // shows through where children are too small to draw
        var tiles = TreemapLayout.Squarify(node.Children, child => (double)child.TotalSize,
            new TreemapRect(rect.X, rect.Y, rect.Width, rect.Height));
        foreach (var tile in tiles)
        {
            Draw(dc, tile.Item,
                new Rect(tile.Rect.X, tile.Rect.Y, Math.Max(0, tile.Rect.Width), Math.Max(0, tile.Rect.Height)));
        }
    }

    private EvaluatedNode? TileAt(Point point)
    {
        foreach (var (node, rect) in _leafTiles)
        {
            if (rect.Contains(point))
                return node;
        }
        return null;
    }

    private static Brush BrushFor(EvaluatedNode node)
    {
        if (node.Status == IncludeStatus.Ignored)
            return IgnoredBrush;
        if (node.Node.IsDirectory)
            return FolderBrush;

        var name = node.Node.Name;
        var dot = name.LastIndexOf('.');
        var extension = dot < 0 ? "" : name[dot..];
        var hash = string.GetHashCode(extension, StringComparison.OrdinalIgnoreCase) & 0x7FFFFFFF;
        return Palette[hash % Palette.Length];
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(new SolidColorBrush(color), thickness);
        pen.Freeze();
        return pen;
    }
}
```

- [ ] **Step 2: Add the treemap below the tree**

In `src/ReBackup.App/Views/IgnorePreviewView.xaml`:

1. Add the namespace to the `<UserControl>` element:

```xml
             xmlns:controls="clr-namespace:ReBackup.App.Controls"
```

2. Replace the row definitions of the `PreviewArea` grid with:

```xml
                <Grid.RowDefinitions>
                    <RowDefinition Height="*" MinHeight="120" />
                    <RowDefinition Height="8" />
                    <RowDefinition Height="220" MinHeight="80" />
                </Grid.RowDefinitions>
```

3. Add after the closing `</ListView>` tag, still inside the `PreviewArea` grid:

```xml
                <GridSplitter Grid.Row="1" Height="8" HorizontalAlignment="Stretch" Background="Transparent" />
                <Border Grid.Row="2" BorderBrush="#ABADB3" BorderThickness="1">
                    <controls:TreemapControl Root="{Binding Preview.Root}"
                                             Selected="{Binding Preview.SelectedNode}" />
                </Border>
```

- [ ] **Step 3: Update the spec**

In `docs/superpowers/specs/2026-09-30-rebackup-design.md` §10.2, tab 3, replace the bullet that starts with `Below the tree: treemap (squarified) of the selected tree node` with:

```
   - Below the tree: treemap (squarified) of the whole source, with ignored areas greyed out and the selected entry outlined. Clicking a rectangle selects that entry and reveals it in the tree; selecting a tree row outlines it in the treemap.
```

- [ ] **Step 4: Build, test, smoke-test**

Run: `dotnet build` (expected: 0 warnings, 0 errors), then `dotnet test tests/ReBackup.Core.Tests` (expected: all pass).
Smoke test: start the built exe, confirm it is still running after 5 s, then stop it.

Manual checklist (for the user; do not click through it as an agent):
1. After **Index now**, the treemap fills the area below the tree. Large files are large rectangles, files of one type share a colour, and ignored entries are grey.
2. Adding a pattern greys the matching rectangles after the short delay.
3. Clicking a rectangle outlines it, expands the tree down to that entry, selects the row and scrolls it into view.
4. Selecting a row in the tree outlines the matching rectangle, when that entry is large enough to be drawn.
5. Hovering a rectangle shows its path, size and status.
6. Dragging the splitter or resizing the window redraws the treemap.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(app): treemap of the indexed source, linked to the tree" -m "Co-Authored-By: Claude <model> <noreply@anthropic.com>"
```

---

## Carry-forward from Phase 2 execution (final review triage)

Fix first in Phase 3 (small, left over from the final fix wave):
- `PreviewRowViewModel.StatusDetail`: the tooltip suffix reads `Not scanned:{Error}`; add the space after the colon.
- `IgnorePattern.AppendCharacterClass`: a negated class that starts with `-` (`[!-x]`) now compiles to the range `[^/-x]`. Emit the slash escaped or at the end of the class, and add a test.

Explicit Phase 3 tasks (the backup runner reuses the index and the evaluator):
- Single-instance guard with restart handoff (from Phase 1).
- Folders that were not scanned (links, nesting deeper than `SourceIndexer.MaxDepth`, unreadable) only carry a free-text `IndexNode.Error` and evaluate as Included with 0 files. The runner must record every such non-ignored node (including `RootNode.Error`) as skipped, or a run reports Completed with subtrees missing. Add a typed reason and a rolled-up count on `EvaluatedNode`.
- An unreadable `.backupignore` is dropped silently; the runner should log a warning.
- Decide how file symlinks and empty included folders are backed up.
- Consider `IndexEvaluator.Evaluate(index, settings, globalDefaults)` so callers cannot forget `index.IgnoreFiles`.

Later / nice to have:
- "Un-ignore this" does nothing for entries below an ignored folder or excluded by a nested `.backupignore` (gitignore semantics); enable it only where it can work.
- Regex patterns have no match timeout (`RegexOptions.NonBacktracking` would fit).
- Treemap: pruned tiny children are left out of the weight sum, so the rest of a folder is drawn slightly too large; only leaf tiles are clickable; a selected entry that is too small to draw has no outline.
- A pattern edit during the very first evaluation after indexing is only applied on the next edit.
- The shared list may drop a plan's row selection when switching plans; the list scrolls to the selection on every re-evaluation.
- No keyboard expand/collapse in the tree; collapsing forgets which descendants were expanded.
- Each keystroke in the pattern box re-validates all plans; indexes and evaluated trees are kept per plan for the whole session (memory on very large sources).
- `ByteSize` shows `1024.0 KB` just below a unit boundary; POSIX character classes (`[[:alpha:]]`) are not supported.
- Test gaps: case-insensitivity rows for each match path, unreadable folder, mid-scan cancel, evaluator status equal to `IgnoreMatcher.Match` for every node.
- Commit c320d92 has its Co-Authored-By line in the subject instead of a trailer.
