using ReBackup.Core.Versions;

namespace ReBackup.App.ViewModels;

/// <summary>
/// A version the Ignore tab's tree is compared with: its size, and the sizes of the entries of one of its folders by
/// <see cref="Key"/> (empty when the version has no such folder). <see cref="ListFolder"/> runs off the UI thread.
/// </summary>
public sealed record PreviewComparison(long RootSize, Func<string, IReadOnlyDictionary<string, long>> ListFolder)
{
    /// <summary>A file and a folder of the same name are different entries.</summary>
    public static string Key(string name, bool isDirectory) => isDirectory ? name + "/" : name;

    /// <summary>Compares with version <paramref name="version"/> of the index <paramref name="open"/> opens.</summary>
    public static PreviewComparison Of(Func<VersionIndex> open, IndexedVersion version) =>
        new(version.TotalBytes, folder =>
        {
            var index = open();
            if (index.FindPath(folder, isDirectory: true) is not { } folderId)
                return new Dictionary<string, long>();
            return index.Children(version.Id, folderId)
                .Where(child => child.InVersion is not null)
                .ToDictionary(child => Key(child.Name, child.IsDirectory), child => child.InVersion!.Size,
                    StringComparer.OrdinalIgnoreCase);
        });
}
