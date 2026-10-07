using ReBackup.Storage;

namespace ReBackup.Core.Tests.TestSupport;

/// <summary>Opens locations with the given function, e.g. to wrap the real storage in a <see cref="FaultyStorage"/>.</summary>
public sealed class TestStorageFactory(Func<StorageLocation, IStorage> open) : IStorageFactory
{
    /// <summary>The real storage of each location, wrapped by <paramref name="wrap"/>.</summary>
    public static TestStorageFactory Wrapping(Func<IStorage, IStorage> wrap) =>
        new(location => wrap(new StorageFactory().Open(location)));

    public IStorage Open(StorageLocation location) => open(location);
}
