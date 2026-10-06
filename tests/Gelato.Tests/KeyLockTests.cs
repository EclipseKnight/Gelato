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

/// <summary>
/// A call that names a stream row (playback, stream, download, subtitles) waits for a sync of the
/// row's movie/episode that is already running, and never starts one:
/// <see cref="KeyLock.JoinIfRunningAsync"/>. A details read starts the refresh without waiting:
/// <see cref="KeyLock.StartSingleFlightInBackground"/>.
/// </summary>
public class KeyLockJoinTests
{
    [Fact]
    public async Task Joining_with_nothing_running_returns_at_once_and_starts_nothing()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();

        var joined = keyLock.JoinIfRunningAsync(key);
        Assert.True(joined.IsCompleted);

        // The join left nothing behind: the next run is a run of its own.
        var runs = 0;
        await keyLock.RunSingleFlightAsync(
            key,
            _ => Task.FromResult(Interlocked.Increment(ref runs))
        );
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task Joining_waits_for_the_running_action_without_running_it_again()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var runs = 0;

        var running = keyLock.RunSingleFlightAsync(
            key,
            async _ =>
            {
                Interlocked.Increment(ref runs);
                await gate.Task;
            }
        );

        var joined = keyLock.JoinIfRunningAsync(key);
        Assert.False(joined.IsCompleted);

        gate.SetResult();
        await joined;
        Assert.True(running.IsCompleted);
        Assert.Equal(1, runs);
    }

    [Fact]
    public void Joining_another_key_does_not_wait()
    {
        var keyLock = new KeyLock();
        var gate = new TaskCompletionSource();
        _ = keyLock.RunSingleFlightAsync(Guid.NewGuid(), _ => gate.Task);

        Assert.True(keyLock.JoinIfRunningAsync(Guid.NewGuid()).IsCompleted);
        gate.SetResult();
    }

    [Fact]
    public async Task A_failed_run_frees_its_key()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var runs = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            keyLock.RunSingleFlightAsync(
                key,
                async _ =>
                {
                    Interlocked.Increment(ref runs);
                    await Task.Yield();
                    throw new InvalidOperationException("sync failed");
                }
            )
        );

        Assert.True(keyLock.JoinIfRunningAsync(key).IsCompleted);
        await keyLock.RunSingleFlightAsync(
            key,
            _ => Task.FromResult(Interlocked.Increment(ref runs))
        );
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task A_background_start_says_whether_it_started_or_joined()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var runs = 0;

        Func<CancellationToken, Task> action = async _ =>
        {
            Interlocked.Increment(ref runs);
            started.TrySetResult();
            await gate.Task;
        };

        Assert.True(keyLock.StartSingleFlightInBackground(key, action));
        Assert.False(keyLock.StartSingleFlightInBackground(key, action));

        // A call that has to wait joins the background run.
        await started.Task;
        var joined = keyLock.JoinIfRunningAsync(key);
        Assert.False(joined.IsCompleted);

        gate.SetResult();
        await joined;
        Assert.Equal(1, runs);

        // Done: the next start is a new run.
        await WaitUntilFree(keyLock, key);
        Assert.True(keyLock.StartSingleFlightInBackground(key, _ => Task.CompletedTask));
    }

    [Fact]
    public async Task A_background_run_does_not_see_the_callers_context()
    {
        var keyLock = new KeyLock();
        var key = Guid.NewGuid();
        var request = new AsyncLocal<string?> { Value = "request" };
        var seen = new TaskCompletionSource<string?>();

        keyLock.StartSingleFlightInBackground(
            key,
            _ =>
            {
                seen.SetResult(request.Value);
                return Task.CompletedTask;
            }
        );

        Assert.Null(await seen.Task);
    }

    private static async Task WaitUntilFree(KeyLock keyLock, Guid key)
    {
        // The entry is removed in the run's own finally, just after the action ends.
        for (var i = 0; i < 100 && !keyLock.JoinIfRunningAsync(key).IsCompleted; i++)
        {
            await Task.Delay(10);
        }
    }
}
