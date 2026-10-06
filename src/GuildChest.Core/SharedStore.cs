using System;
using System.Collections.Generic;
using System.Linq;

namespace GuildChest.Core;

public enum AccessResult
{
    Accepted, Busy, UnknownChest, InvalidSession, StaleRevision, InvalidSequence,
    AccessDenied, InvalidInventory, ItemMismatch, InvalidDimensions, RecoveryPending
}

public sealed class StoreSnapshot
{
    public byte[] Inventory { get; }
    public long Revision { get; }
    public StoreSnapshot(byte[] inventory, long revision) { Inventory = (byte[])inventory.Clone(); Revision = revision; }
}

public sealed class Lease
{
    public long Peer { get; }
    public string Chest { get; }
    public string Token { get; } = Guid.NewGuid().ToString("N");
    public double ExpiresAt { get; internal set; }
    internal long Sequence;
    internal byte[]? LastPayload;
    public Lease(long peer, string chest, double expiresAt) { Peer = peer; Chest = chest; ExpiresAt = expiresAt; }
}

/// <summary>Single-threaded world authority. The Unity adapter owns serialization and access checks.</summary>
public sealed class SharedStore
{
    public const double LeaseSeconds = 30;
    private readonly HashSet<string> chests = new(StringComparer.Ordinal);
    private readonly byte[] empty;
    private byte[] inventory;
    private long revision;
    private readonly Dictionary<string, byte[]> receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> receiptOrder = new();
    public Lease? ActiveLease { get; private set; }
    public int ChestCount => chests.Count;
    public SharedStore(byte[] emptyInventory, byte[] savedInventory, long savedRevision)
    {
        empty = (byte[])emptyInventory.Clone(); inventory = (byte[])savedInventory.Clone(); revision = savedRevision;
    }
    public StoreSnapshot Snapshot() => new(inventory, revision);
    public bool Register(string chest) => chests.Add(chest);
    public bool Contains(string chest) => chests.Contains(chest);

    public AccessResult Open(long peer, string chest, double now, out Lease? lease)
    {
        Expire(now); lease = null;
        if (!chests.Contains(chest)) return AccessResult.UnknownChest;
        if (ActiveLease != null)
        {
            if (ActiveLease.Peer != peer || ActiveLease.Chest != chest) return AccessResult.Busy;
            ActiveLease.ExpiresAt = now + LeaseSeconds; lease = ActiveLease; return AccessResult.Accepted;
        }
        lease = ActiveLease = new Lease(peer, chest, now + LeaseSeconds);
        return AccessResult.Accepted;
    }

    public AccessResult Commit(long peer, string token, long expectedRevision, long sequence, byte[] payload, double now)
    {
        if (TryReplay(peer, token, sequence, payload, out var replay)) return replay;
        if (!Authorized(peer, token, now)) return AccessResult.InvalidSession;
        var lease = ActiveLease!;
        // A retry must match its original payload, even if its expected revision is now old.
        if (sequence == lease.Sequence && lease.LastPayload != null)
            return payload.SequenceEqual(lease.LastPayload) ? AccessResult.Accepted : AccessResult.InvalidSequence;
        if (sequence != lease.Sequence + 1) return AccessResult.InvalidSequence;
        if (expectedRevision != revision) return AccessResult.StaleRevision;
        inventory = (byte[])payload.Clone(); revision++;
        lease.Sequence = sequence; lease.LastPayload = (byte[])payload.Clone(); lease.ExpiresAt = now + LeaseSeconds;
        string key = ReceiptKey(peer, token, sequence);
        receipts.Add(key, (byte[])payload.Clone()); receiptOrder.Enqueue(key);
        while (receiptOrder.Count > 256) receipts.Remove(receiptOrder.Dequeue());
        return AccessResult.Accepted;
    }

    private static string ReceiptKey(long peer, string token, long sequence) => $"{peer}/{token}/{sequence}";
    public bool TryReplay(long peer, string token, long sequence, byte[] payload, out AccessResult result)
    {
        result = AccessResult.InvalidSession;
        if (!receipts.TryGetValue(ReceiptKey(peer, token, sequence), out var previous)) return false;
        result = previous.SequenceEqual(payload) ? AccessResult.Accepted : AccessResult.InvalidSequence;
        return true;
    }

    public bool Heartbeat(long peer, string token, double now)
    {
        if (!Authorized(peer, token, now)) return false;
        ActiveLease!.ExpiresAt = now + LeaseSeconds; return true;
    }
    public bool Close(long peer, string token)
    {
        if (ActiveLease?.Peer != peer || ActiveLease.Token != token) return false;
        ActiveLease = null; return true;
    }
    public void Disconnect(long peer) { if (ActiveLease?.Peer == peer) ActiveLease = null; }
    public void Expire(double now) { if (ActiveLease != null && now >= ActiveLease.ExpiresAt) ActiveLease = null; }
    public bool Authorized(long peer, string token, double now)
    {
        Expire(now); return ActiveLease?.Peer == peer && ActiveLease.Token == token;
    }

    /// <summary>Called for actual world-object removal, never for scene unloading.</summary>
    public bool Remove(string chest, out StoreSnapshot? spill)
    {
        spill = null;
        if (!chests.Remove(chest)) return false;
        if (ActiveLease?.Chest == chest) ActiveLease = null;
        if (chests.Count == 0)
        {
            spill = Snapshot(); inventory = (byte[])empty.Clone(); revision++;
        }
        return true;
    }
}
