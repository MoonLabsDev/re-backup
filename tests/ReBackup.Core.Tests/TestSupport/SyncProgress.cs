namespace ReBackup.Core.Tests.TestSupport;

/// <summary>Reports synchronously on the calling thread (<see cref="Progress{T}"/> would post to the thread pool).</summary>
public sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
