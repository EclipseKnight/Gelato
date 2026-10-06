using Xunit;

namespace Gelato.Tests;

/// <summary>
/// The background stream refresh relies on <see cref="KeyLock.RunSingleFlightAsync"/> to never
/// sync one movie/episode twice at a time, and to let a waiting call join a running refresh.
/// </summary>
public class KeyLockTests
{
    [Fact]
    public async Task A_second_call_for_the_same_key_joins_the_running_one()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var runs = 0;

        Func<CancellationToken, Task> action = async _ =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
        };

        var first = keyLock.RunSingleFlightAsync(key, action);
        var second = keyLock.RunSingleFlightAsync(key, action);

        Assert.Same(first, second);
        Assert.False(second.IsCompleted);

        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Another_key_runs_on_its_own()
    {
        var keyLock = new KeyLock();
        var gate = new TaskCompletionSource();
        var runs = 0;

        Func<CancellationToken, Task> action = async _ =>
        {
            Interlocked.Increment(ref runs);
            await gate.Task;
        };

        var a = keyLock.RunSingleFlightAsync(Guid.NewGuid(), action);
        var b = keyLock.RunSingleFlightAsync(Guid.NewGuid(), action);

        gate.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task The_key_is_free_again_once_the_run_ends()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var runs = 0;

        await keyLock.RunSingleFlightAsync(
            key,
            _ => Task.FromResult(Interlocked.Increment(ref runs))
        );
        await keyLock.RunSingleFlightAsync(
            key,
            _ => Task.FromResult(Interlocked.Increment(ref runs))
        );

        Assert.Equal(2, runs);
    }
}
