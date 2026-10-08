using System;
using System.Linq;
using UnityEngine;

namespace GuildChest;

// Read-only stock for previews; mutations always obtain a fresh leased snapshot.
internal static class StoragePreview
{
    private static Container? snapshotChest;
    private static Inventory? snapshot;
    private static long peekSequence;
    private static long pendingPeek, snapshotRevision = -1;
    private static ZDOID peekChest;
    private static float nextPeek, peekSentTime, snapshotTime;
    internal static Container? Chest => snapshotChest;
    internal static Inventory? Snapshot => snapshot;
    internal static float UpdatedAt => snapshotTime;
    internal static void Reset()
    {
        snapshot = null; snapshotChest = null;
        nextPeek = snapshotTime = 0; pendingPeek = 0; snapshotRevision = -1;
    }
    internal static void Tick()
    {
        if (!CraftyBoxesAdapter.Installed || !ZNet.instance || !Player.m_localPlayer || Player.m_localPlayer.IsDead() || Time.unscaledTime < nextPeek) return;
        nextPeek = Time.unscaledTime + Protocol.PollInterval;
        try
        {
            var selected = CraftyBoxesAdapter.Nearby().Select(ContainerBindings.ContainerFor).Where(chest => Plugin.IsGuild(chest) &&
                Vector3.Distance(chest!.transform.position, Player.m_localPlayer.transform.position) <= Plugin.AutomationRange.Value)
                .OrderBy(chest => Vector3.Distance(chest!.transform.position, Player.m_localPlayer.transform.position)).FirstOrDefault();
            if (!selected) { snapshot = null; snapshotChest = null; pendingPeek = 0; return; }
            if (snapshotChest != selected) { snapshot = null; pendingPeek = 0; }
            snapshotChest = selected;
            // A new poll must not supersede a reply still travelling from the host.
            // Retry a lost request after ten seconds; late replies cannot match it.
            if (pendingPeek != 0 && Time.unscaledTime - peekSentTime < Protocol.PeekTimeout) return;
            pendingPeek = ++peekSequence; peekSentTime = Time.unscaledTime;
            peekChest = selected!.GetComponent<ZNetView>().GetZDO().m_uid;
            Plugin.RequestRpc.SendPackage(Plugin.ServerId, Protocol.Header(Operation.Peek, peekChest, "", pendingPeek));
        }
        catch (Exception exception) { snapshot = null; pendingPeek = 0; Plugin.Error(exception); }
    }
    internal static void ReceiveSnapshot(ZDOID id, long sequence, GuildChest.Core.AccessResult result, long revision, byte[] bytes)
    {
        if (!snapshotChest || pendingPeek == 0 || sequence != pendingPeek || id != peekChest || snapshotChest!.GetComponent<ZNetView>().GetZDO()?.m_uid != id) return;
        pendingPeek = 0;
        nextPeek = Math.Min(nextPeek, Math.Max(Time.unscaledTime, peekSentTime + Protocol.PollInterval));
        if (result == GuildChest.Core.AccessResult.Accepted) RefreshSnapshot(bytes, revision);
        else { snapshot = null; CraftyBoxesAdapter.InvalidateCounts(); }
    }
    internal static void RefreshSnapshot(byte[] bytes, long revision)
    {
        // A Peek sent before a commit can arrive after that commit's ACK.
        if (revision < snapshotRevision) return;
        snapshotRevision = revision;
        if (!snapshotChest) return;
        snapshot = InventoryCodec.Read(bytes); InventoryAccess.Protect(snapshot); snapshotTime = Time.unscaledTime;
        CraftyBoxesAdapter.InvalidateCounts();
    }
    internal static Inventory? For(Container? chest) => StorageScopes.PreviewDepth > 0 && StorageScopes.ConsumptionDepth == 0 && !Client.Pending && chest &&
        chest == snapshotChest && Time.unscaledTime - snapshotTime < Protocol.PreviewLifetime && Player.m_localPlayer &&
        Vector3.Distance(chest.transform.position, Player.m_localPlayer.transform.position) <= Plugin.AutomationRange.Value ? snapshot : null;
}
