using GuildChest.Core;
using Xunit;

namespace GuildChest.Tests;

public class SharedStoreTests
{
    private static SharedStore Create()
    {
        var store = new SharedStore(new byte[] { 0 }, new byte[] { 42 }, 7);
        store.Register("near"); store.Register("unloaded"); return store;
    }

    [Fact] public void OneLeaseAcrossAllChestsAndPeerCannotStealOrCloseIt()
    {
        var store = Create();
        Assert.Equal(AccessResult.Accepted, store.Open(1, "near", 0, out var lease));
        Assert.Equal(AccessResult.Busy, store.Open(2, "unloaded", 1, out _));
        Assert.False(store.Close(2, lease!.Token));
        Assert.Equal(AccessResult.InvalidSession, store.Commit(2, lease.Token, 7, 1, new byte[] { 3 }, 2));
        Assert.True(store.Close(1, lease.Token));
        Assert.Equal(AccessResult.Accepted, store.Open(2, "unloaded", 3, out _));
    }

    [Fact] public void HeartbeatRenewsButExpiredLeaseCannotMutateAfterNewOwner()
    {
        var store = Create(); store.Open(1, "near", 0, out var lease);
        Assert.True(store.Heartbeat(1, lease!.Token, 20));
        Assert.Equal(AccessResult.Busy, store.Open(2, "unloaded", 40, out _));
        Assert.Equal(AccessResult.Accepted, store.Open(2, "unloaded", 50, out _));
        Assert.Equal(AccessResult.InvalidSession, store.Commit(1, lease.Token, 7, 1, new byte[] { 9 }, 51));
        Assert.Equal(new byte[] { 42 }, store.Snapshot().Inventory);
    }

    [Fact] public void DuplicateCommitIsIdempotentButChangedOrOutOfOrderRetriesAreRejected()
    {
        var store = Create(); store.Open(1, "near", 0, out var lease);
        Assert.Equal(AccessResult.Accepted, store.Commit(1, lease!.Token, 7, 1, new byte[] { 9 }, 1));
        Assert.Equal(AccessResult.Accepted, store.Commit(1, lease.Token, 7, 1, new byte[] { 9 }, 2));
        Assert.Equal(8, store.Snapshot().Revision);
        Assert.Equal(AccessResult.InvalidSequence, store.Commit(1, lease.Token, 8, 1, new byte[] { 8 }, 3));
        Assert.Equal(AccessResult.InvalidSequence, store.Commit(1, lease.Token, 8, 3, new byte[] { 8 }, 3));
        Assert.Equal(AccessResult.StaleRevision, store.Commit(1, lease.Token, 7, 2, new byte[] { 8 }, 3));
    }

    [Fact] public void UnloadedChestPreventsSpillAndFinalRemovalSpillsOnlyOnce()
    {
        var store = Create();
        Assert.False(store.Register("near"));
        Assert.True(store.Remove("near", out var none)); Assert.Null(none);
        Assert.Equal(new byte[] { 42 }, store.Snapshot().Inventory);
        Assert.True(store.Remove("unloaded", out var spill));
        Assert.Equal(new byte[] { 42 }, spill!.Inventory);
        Assert.Equal(new byte[] { 0 }, store.Snapshot().Inventory);
        Assert.False(store.Remove("unloaded", out var duplicate)); Assert.Null(duplicate);
    }

    [Fact] public void RemovingOccupiedChestRevokesLeaseWithoutEmptyingOtherChest()
    {
        var store = Create(); store.Open(1, "near", 0, out var lease);
        store.Remove("near", out _);
        Assert.Equal(AccessResult.InvalidSession, store.Commit(1, lease!.Token, 7, 1, new byte[] { 9 }, 1));
        Assert.Equal(AccessResult.Accepted, store.Open(2, "unloaded", 2, out _));
    }

    [Fact] public void SavedInventoryRoundTripsAndWorldStoresAreIsolated()
    {
        var first = Create(); first.Open(1, "near", 0, out var lease);
        first.Commit(1, lease!.Token, 7, 1, new byte[] { 11 }, 1);
        var snapshot = first.Snapshot();
        var restored = new SharedStore(new byte[] { 0 }, snapshot.Inventory, snapshot.Revision);
        Assert.Equal(new byte[] { 11 }, restored.Snapshot().Inventory);
        Assert.Null(restored.ActiveLease);
        Assert.Equal(new byte[] { 42 }, Create().Snapshot().Inventory);
        snapshot.Inventory[0] = 99;
        Assert.Equal(new byte[] { 11 }, first.Snapshot().Inventory);
        Assert.Equal(new byte[] { 11 }, restored.Snapshot().Inventory);
    }

    [Fact] public void UnknownChestAndExpiredHeartbeatCannotAcquireOrRenewAccess()
    {
        var store = Create();
        Assert.Equal(AccessResult.UnknownChest, store.Open(1, "nonexistent", 0, out _));
        store.Open(1, "near", 0, out var lease);
        Assert.False(store.Heartbeat(1, lease!.Token, 30));
    }

    [Fact] public void AcceptedCommitCanBeAcknowledgedAfterCloseExpiryOrChestDestruction()
    {
        var store = Create(); store.Open(1, "near", 0, out var lease);
        store.Commit(1, lease!.Token, 7, 1, new byte[] { 9 }, 1);
        store.Remove("near", out _); store.Remove("unloaded", out _);
        Assert.Equal(AccessResult.Accepted, store.Commit(1, lease.Token, 7, 1, new byte[] { 9 }, 100));
        Assert.Equal(new byte[] { 0 }, store.Snapshot().Inventory);
        Assert.Equal(9, store.Snapshot().Revision);
    }
}
