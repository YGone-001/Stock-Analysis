using System;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Outcome of a retry loop: whether it eventually succeeded, how many attempts were made,
/// and the last error if all attempts failed.
/// </summary>
public readonly record struct SparrowRetryOutcome(bool Success, int Attempts, Exception? LastError);

/// <summary>
/// Retry + exponential-backoff policy shared by every Sparrow data-fetching path.
/// Pure and transport-agnostic so it can be unit-tested without a network.
/// A cancellation always rethrows <see cref="OperationCanceledException"/> rather than
/// being reported as a failed attempt.
/// </summary>
public static class SparrowRetryPolicy
{
    public const int DefaultMaxAttempts = 3;
    public static readonly TimeSpan DefaultBaseDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>Exponential backoff: base * 2^(attempt-1), capped at <see cref="MaxDelay"/>.</summary>
    public static TimeSpan ExponentialBackoff(int attempt, TimeSpan? baseDelay = null)
    {
        if (attempt <= 0)
        {
            return TimeSpan.Zero;
        }

        TimeSpan delayBase = baseDelay ?? DefaultBaseDelay;
        double factor = Math.Pow(2, attempt - 1);
        double milliseconds = Math.Min(delayBase.TotalMilliseconds * factor, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> repeatedly until it reports success, the attempts are
    /// exhausted, or the token is cancelled. A thrown exception is captured and treated as a
    /// failed attempt; only cancellation is rethrown.
    /// </summary>
    public static async System.Threading.Tasks.Task<SparrowRetryOutcome> RetryAsync(
        Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<bool>> attempt,
        int maxAttempts = DefaultMaxAttempts,
        Func<int, TimeSpan>? backoff = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        Exception? lastError = null;
        for (int index = 1; index <= maxAttempts; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await attempt(cancellationToken).ConfigureAwait(false))
                {
                    return new SparrowRetryOutcome(true, index, null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (index < maxAttempts)
            {
                TimeSpan delay = backoff?.Invoke(index) ?? ExponentialBackoff(index);
                if (delay > TimeSpan.Zero)
                {
                    await System.Threading.Tasks.Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return new SparrowRetryOutcome(false, maxAttempts, lastError);
    }
}
