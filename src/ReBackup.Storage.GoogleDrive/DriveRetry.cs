namespace ReBackup.Storage.GoogleDrive;

/// <summary>
/// Repeats Drive calls that failed for their rate (429, 403 rate limit) and, for calls that are safe to repeat, on server and
/// network failures: up to <see cref="MaxAttempts"/> attempts, waiting 1, 2, 4, 8, 16 s (at most <see cref="MaxDelay"/>) plus up to
/// a second of jitter between them. The waits run on the <see cref="TimeProvider"/> and end at once when the caller cancels.
/// After the last attempt the failure is thrown as it came.
/// </summary>
internal sealed class DriveRetry(TimeProvider time)
{
    public const int MaxAttempts = 6;

    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(32);

    /// <summary>Runs <paramref name="call"/>; <paramref name="repeatable"/> false retries rate limits only (a call that creates something: its caller checks what landed before repeating it).</summary>
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct, bool repeatable = true)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested && IsRetried(ex, repeatable, ct))
            {
                await Task.Delay(DelayFor(attempt), time, ct).ConfigureAwait(false);
            }
        }
    }

    public Task RunAsync(Func<CancellationToken, Task> call, CancellationToken ct, bool repeatable = true) =>
        RunAsync(async c =>
        {
            await call(c).ConfigureAwait(false);
            return true;
        }, ct, repeatable);

    /// <summary>The wait after failed attempt <paramref name="attempt"/> (1-based): 2^(attempt-1) s, at most <see cref="MaxDelay"/>, plus jitter below 1 s.</summary>
    internal static TimeSpan DelayFor(int attempt)
    {
        var seconds = Math.Min(Math.Pow(2, attempt - 1), MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble() * 0.999);
    }

    private static bool IsRetried(Exception ex, bool repeatable, CancellationToken ct) =>
        (ex is DriveApiException api && DriveErrors.IsRateLimit(api))
        || (repeatable && DriveErrors.IsServerOrNetworkFailure(ex, ct));
}
