using Gelato.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Xunit;

namespace Gelato.Tests;

public class StreamSyncCacheTests
{
    private static readonly TimeSpan StreamTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan NoStreamsTtl = TimeSpan.FromMinutes(10);

    private readonly ManualClock _clock = new();
    private readonly MemoryCache _memory;
    private TimeSpan _noStreamsTtl = NoStreamsTtl;
    private readonly StreamSyncCache _cache;
    private readonly Guid _itemId = Guid.NewGuid();

    public StreamSyncCacheTests()
    {
#pragma warning disable CS0618 // The clock is the cache's way to move time in tests.
        _memory = new MemoryCache(new MemoryCacheOptions { Clock = _clock });
#pragma warning restore CS0618
        _cache = new StreamSyncCache(_memory, () => StreamTtl, () => _noStreamsTtl);
    }

    [Fact]
    public void Nothing_synced_is_due()
    {
        Assert.Equal(StreamSyncState.Due, _cache.Get("user:item", _itemId));
    }

    [Fact]
    public void Streams_found_are_kept_for_the_stream_ttl()
    {
        _cache.Set("user:item", 5);

        _clock.Advance(StreamTtl - TimeSpan.FromSeconds(1));
        Assert.Equal(StreamSyncState.Synced, _cache.Get("user:item", _itemId));

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamSyncState.Due, _cache.Get("user:item", _itemId));
    }

    [Fact]
    public void No_streams_is_remembered_for_the_shorter_time_then_synced_again()
    {
        _cache.Set("user:item", 0);
        Assert.Equal(StreamSyncState.NoStreams, _cache.Get("user:item", _itemId));

        _clock.Advance(NoStreamsTtl - TimeSpan.FromSeconds(1));
        Assert.Equal(StreamSyncState.NoStreams, _cache.Get("user:item", _itemId));

        // After the window the title is asked for again, so streams that appeared are found.
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(StreamSyncState.Due, _cache.Get("user:item", _itemId));

        _cache.Set("user:item", 3);
        Assert.Equal(StreamSyncState.Synced, _cache.Get("user:item", _itemId));
    }

    [Fact]
    public void No_streams_is_not_remembered_when_turned_off()
    {
        _noStreamsTtl = TimeSpan.Zero;

        _cache.Set("user:item", 0);

        Assert.Equal(StreamSyncState.Due, _cache.Get("user:item", _itemId));
    }

    [Fact]
    public void No_streams_is_never_kept_longer_than_streams_found()
    {
        _noStreamsTtl = TimeSpan.FromHours(5);

        _cache.Set("user:item", 0);
        _clock.Advance(StreamTtl + TimeSpan.FromSeconds(1));

        Assert.Equal(StreamSyncState.Due, _cache.Get("user:item", _itemId));
    }

    [Fact]
    public void Marks_are_per_key()
    {
        _cache.Set("userA:item", 0);
        _cache.Set("userB:item", 4);

        Assert.Equal(StreamSyncState.NoStreams, _cache.Get("userA:item", _itemId));
        Assert.Equal(StreamSyncState.Synced, _cache.Get("userB:item", _itemId));
        Assert.Equal(StreamSyncState.Due, _cache.Get("userC:item", _itemId));
    }

    [Fact]
    public void A_reset_makes_both_kinds_of_mark_due()
    {
        _cache.Set("userA:item", 0);
        _cache.Set("userB:item", 4);

        _cache.Reset(_itemId);

        Assert.True(_cache.WasReset(_itemId));
        Assert.Equal(StreamSyncState.Due, _cache.Get("userA:item", _itemId));
        Assert.Equal(StreamSyncState.Due, _cache.Get("userB:item", _itemId));
    }

    [Fact]
    public void A_sync_after_a_reset_counts()
    {
        _cache.Reset(_itemId);

        _cache.Set("user:item", 2);

        Assert.Equal(StreamSyncState.Synced, _cache.Get("user:item", _itemId));
    }

    [Fact]
    public void Syncs_and_resets_are_ordered_by_sequence_not_by_the_clock()
    {
        // Upstream #268: with the clock stepped back between a sync and a reset, a time stamp
        // made the sync look newer. Back-to-back calls, with no time between them, show the
        // order is the order of the calls.
        for (var i = 0; i < 100; i++)
        {
            _cache.Set("user:item", 3);
            _cache.Reset(_itemId);
            Assert.Equal(StreamSyncState.Due, _cache.Get("user:item", _itemId));

            _cache.Set("user:item", 3);
            Assert.Equal(StreamSyncState.Synced, _cache.Get("user:item", _itemId));
        }
    }

    private sealed class ManualClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
