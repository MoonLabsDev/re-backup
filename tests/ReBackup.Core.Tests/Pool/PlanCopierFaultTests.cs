using System.Text;
using System.Text.Json;
using FluentAssertions;
using ReBackup.Core.Backup;
using ReBackup.Core.Plans;
using ReBackup.Core.Pool;
using ReBackup.Core.Tests.TestSupport;
using ReBackup.Core.Versions;
using ReBackup.Shared.Json;
using ReBackup.Storage;
using ReBackup.Storage.InMemory;
using Xunit.Sdk;
using static ReBackup.Core.Tests.TestSupport.PoolVersions;

namespace ReBackup.Core.Tests.Pool;

/// <summary>
/// Fault injection on "Copy plan…" (spec 13): a copy fails at its N-th write or delete on the new target, for every N.
/// Whatever happens, every version the new target lists as the copy's own restores byte-exact (nothing incomplete looks
/// complete), and a resumed copy (after a person released a lock left behind) finishes with every version restorable.
/// </summary>
public class PlanCopierFaultTests
{
    private const string SourceRoot = @"C:\backup\projects";
    private const string CopyRoot = @"D:\nas\projects";
    private const int MaxSteps = 1000;

    private static readonly string[] Names = ["2026_01_01-10_00 Projects", "2026_01_02-10_00 Projects", "2026_01_03-10_00 Projects"];

    /// <summary>The files of version <paramref name="i"/>: one content of its own, one shared by all, one in a folder.</summary>
    private static Dictionary<string, string> FilesOf(int i) => new(StringComparer.Ordinal)
    {
        ["a.txt"] = $"alpha {i}",
        ["shared.txt"] = "shared by every version",
        ["docs/b.txt"] = i == 2 ? "beta 2" : "beta",
    };

    /// <summary>The plan's target with the three versions, full (format 2, xxh64) or a pool.</summary>
    private static (BackupPlan Plan, InMemoryStorage Target) Source(bool pool)
    {
        var plan = new BackupPlan
        {
            Id = "source-plan",
            Name = "Projects",
            Incremental = pool,
            Source = StorageLocation.FileSystem(@"C:\data"),
            Target = StorageLocation.FileSystem(SourceRoot),
        };
        var target = new InMemoryStorage();
        if (pool)
            target.AddFile(PoolLayout.StoreFile, Bytes($"{{\"format\":\"pool\",\"hash\":\"xxh128\",\"version\":1,\"planId\":\"{plan.Id}\"}}"));
        for (var i = 0; i < Names.Length; i++)
        {
            if (pool)
            {
                PoolVersions.Write(target, Names[i], plan.Id, plan.Name, FilesOf(i).Select(f => (f.Key, f.Value)), directories: ["docs"]);
                continue;
            }
            var manifest = new BackupManifest { PlanId = plan.Id, PlanName = plan.Name, CreatedUtc = Mtime, Source = @"C:\data", Directories = ["docs"] };
            foreach (var (path, content) in FilesOf(i))
            {
                target.AddFile(StoragePath.Combine(Names[i], path), Bytes(content), Mtime);
                manifest.Files.Add(new ManifestFile(path, Bytes(content).Length, Mtime,
                    "xxh64:" + Convert.ToHexStringLower(System.IO.Hashing.XxHash64.Hash(Bytes(content)))));
            }
            manifest.FileCount = manifest.Files.Count;
            manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
            target.AddFile(StoragePath.Combine(Names[i], VersionMarkerNames.Manifest),
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, JsonDefaults.Options)));
        }
        return (plan, target);
    }

    private static PlanCopier Copier(IStorage from, IStorage to) =>
        new(new TestStorageFactory(location => location.Path == SourceRoot ? from : to), "PC1", TimeProvider.System);

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    public async Task Fault_at_every_write_keeps_complete_versions_restorable_and_resume_finishes(bool sourcePool,
        bool copyIncremental, bool storageDiesAtFault)
    {
        var completedWithoutFault = false;
        for (var n = 1; n <= MaxSteps && !completedWithoutFault; n++)
        {
            var (plan, from) = Source(sourcePool);
            var to = new InMemoryStorage();
            var copy = PlanCopier.MakeCopy(plan, plan.Name, StorageLocation.FileSystem(CopyRoot), copyIncremental);
            var step = 0;
            var stepAtFault = n;
            var faulty = new FaultyStorage(to)
            {
                Before = (operation, path) =>
                {
                    if (operation is not ("create" or "commit" or "delete" or "ensure"))
                        return;
                    step++;
                    if (step == stepAtFault || (storageDiesAtFault && step > stepAtFault))
                        throw new StorageAccessDeniedException(path);
                },
            };

            var result = await Copier(from, faulty).CopyAsync(plan, copy, null, CancellationToken.None);
            completedWithoutFault = step < n;
            var context = $"fault at step {n}{(storageDiesAtFault ? " and after" : "")}: " + string.Join("; ", result.Failures);
            if (completedWithoutFault)
            {
                result.Should().BeEquivalentTo(new PlanCopyResult(Names.Length, 0, [], false), context);
                result.Finished.Should().BeTrue(context);
            }
            var listed = await AssertListedVersionsRestoreAsync(to, copy, context);
            if (listed < Names.Length)
                result.Finished.Should().BeFalse($"{context}: only {listed} versions are complete");

            // A person releases a lock the copy could not remove; the copy is continued and finishes.
            await TargetLock.ForceReleaseAsync(to, CancellationToken.None);
            await TargetLock.ForceReleaseAsync(from, CancellationToken.None);
            var resumed = await Copier(from, to).CopyAsync(plan, copy, null, CancellationToken.None);
            resumed.Finished.Should().BeTrue($"{context}; resumed: {string.Join("; ", resumed.Failures)}");
            resumed.Failures.Should().BeEmpty(context);
            (await AssertListedVersionsRestoreAsync(to, copy, context + "; after the resumed copy")).Should().Be(Names.Length);
            to.Files.Should().NotContain(f => f.EndsWith(VersionMarkerNames.Pending, StringComparison.Ordinal) ||
                                              f == PoolLayout.LockFile, context);
            from.Files.Should().NotContain(PoolLayout.LockFile, context);
        }

        completedWithoutFault.Should().BeTrue($"a copy without a fault needs fewer than {MaxSteps} writes and deletes");
    }

    /// <summary>Every version the target lists as the copy's own restores to its source files; returns how many.</summary>
    private static async Task<int> AssertListedVersionsRestoreAsync(InMemoryStorage target, BackupPlan copy, string context)
    {
        var owned = (await VersionCatalog.ListAsync(target, copy.Id, copy.Name)).Where(v => v.IsOwned).ToList();
        foreach (var version in owned)
        {
            var index = Array.IndexOf(Names, version.Name);
            index.Should().BeGreaterThanOrEqualTo(0, $"{context}: {version.Name} is not a version of the plan");
            Dictionary<string, string> restored;
            try
            {
                var view = await VersionViews.OpenAsync(target, version.Path, CancellationToken.None);
                restored = await RestoreFromAsync(view, version.Path);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException)
            {
                throw new XunitException($"{context}: {version.Name} does not restore: {ex.Message}");
            }
            restored.Should().Equal(FilesOf(index), $"{context}: {version.Name}");
        }
        return owned.Count;
    }
}
