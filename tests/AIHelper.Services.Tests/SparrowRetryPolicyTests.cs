using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowRetryPolicyTests
{
    private static readonly Func<int, TimeSpan> NoDelay = _ => TimeSpan.Zero;

    [Fact]
    public async Task RetryAsync_SucceedsImmediately_ReportsOneAttempt()
    {
        int calls = 0;
        SparrowRetryOutcome outcome = await SparrowRetryPolicy.RetryAsync(
            _ => { calls++; return System.Threading.Tasks.Task.FromResult(true); },
            maxAttempts: 3,
            backoff: NoDelay);

        Assert.True(outcome.Success);
        Assert.Equal(1, outcome.Attempts);
        Assert.Null(outcome.LastError);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetryAsync_RetriesUntilSuccess()
    {
        int calls = 0;
        SparrowRetryOutcome outcome = await SparrowRetryPolicy.RetryAsync(
            _ =>
            {
                calls++;
                return System.Threading.Tasks.Task.FromResult(calls >= 2);
            },
            maxAttempts: 3,
            backoff: NoDelay);

        Assert.True(outcome.Success);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RetryAsync_ExhaustsAttempts_ReportsFailureAndLastError()
    {
        var expected = new InvalidOperationException("boom");
        SparrowRetryOutcome outcome = await SparrowRetryPolicy.RetryAsync(
            _ => System.Threading.Tasks.Task.FromException<bool>(expected),
            maxAttempts: 3,
            backoff: NoDelay);

        Assert.False(outcome.Success);
        Assert.Equal(3, outcome.Attempts);
        Assert.Same(expected, outcome.LastError);
    }

    [Fact]
    public async Task RetryAsync_RethrowsOnCancellation()
    {
        using var cts = new System.Threading.CancellationTokenSource();
        var token = cts.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await SparrowRetryPolicy.RetryAsync(
                _ =>
                {
                    cts.Cancel();
                    return System.Threading.Tasks.Task.FromResult(false);
                },
                maxAttempts: 3,
                backoff: NoDelay,
                cancellationToken: token);
        });
    }

    [Fact]
    public void ExponentialBackoff_CapsAtMaximumDelay()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(250), SparrowRetryPolicy.ExponentialBackoff(1));
        Assert.Equal(TimeSpan.FromMilliseconds(500), SparrowRetryPolicy.ExponentialBackoff(2));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), SparrowRetryPolicy.ExponentialBackoff(3));
        Assert.Equal(SparrowRetryPolicy.MaxDelay, SparrowRetryPolicy.ExponentialBackoff(10));
    }
}
