using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GuildChest.Core;
using HarmonyLib;
using UnityEngine;

namespace GuildChest;

internal enum QuickAction { None, Stack, Take, Automation }

internal static class Client
{
    internal static Container? Chest;
    internal static bool Pending => transaction != null;
    internal static bool HasSession => token.Length != 0;
    internal static Inventory? View;
    private sealed class OpenRequest
    {
        internal Container Chest = null!;
        internal Player Player = null!;
        internal Inventory Inventory = null!;
        internal ZDOID Id;
        internal long Sequence;
        internal float Started;
        internal QuickAction Action;
        internal Action<Inventory, Inventory>? Transfer;
        internal TransferCompletion? Completion;
    }
    private static OpenRequest? opening;
    private static long openRequest;
    private static ZDOID chestId;
    private static string token = "";
    private static long revision, sequence;
    private static float nextHeartbeat, nextRetry, lastReply;
    private static bool closeWanted;
    private static bool deferredDeath;
    private static Transaction? transaction;
    private static bool finishing, resetting;
    private const float OpenTimeout = 30f, HeartbeatInterval = 5f, RetryInterval = 2f;
    internal static bool Applying;
    internal static bool Opening => opening != null;

    internal sealed class Transaction
    {
        internal readonly Inventory PlayerStage;
        internal readonly List<ItemDrop.ItemData> PlayerOriginals;
        internal readonly Dictionary<ItemDrop.ItemData, ItemDrop.ItemData> Bindings = new();
        internal readonly Inventory SharedStage;
        internal readonly byte[] PlayerBefore;
        internal byte[] Request = Array.Empty<byte>();
        internal long Sequence;
        internal TransferCompletion? Completed;
        internal readonly TransferLifecycle Lifecycle = new();
        internal readonly Player Owner;
        internal readonly Inventory PlayerInventory;
        internal long ReplyRevision;
        internal byte[] ReplyInventory = Array.Empty<byte>();
        internal string ReplyReason = "";
        private List<ItemDrop.ItemData> removed = new();
        private bool notified;
        internal Transaction()
        {
            Owner = Player.m_localPlayer; PlayerInventory = Owner.GetInventory();
            var player = PlayerInventory;
            PlayerBefore = InventoryCodec.Save(player);
            PlayerOriginals = player.GetAllItems().ToList();
            PlayerStage = InventoryCodec.Clone(player); SharedStage = InventoryCodec.Clone(View!);
            for (int i = 0; i < PlayerOriginals.Count; i++) Bindings.Add(PlayerStage.GetAllItems()[i], PlayerOriginals[i]);
        }
        internal void Apply()
        {
            if (!Owner || Owner != Player.m_localPlayer || Owner.GetInventory() != PlayerInventory)
                throw new InvalidOperationException("The original player inventory is no longer available.");
            // Prepare clones and list capacity before changing any live item. The
            // mutation below has no game callbacks; notification failures cannot
            // leave a partially applied transaction eligible for an RPC retry.
            var finalItems = PlayerStage.GetAllItems().Select(staged =>
            {
                if (Bindings.TryGetValue(staged, out var original)) return original;
                var item = staged.Clone(); item.m_equipped = false; return item;
            }).ToList();
            var remaining = new HashSet<ItemDrop.ItemData>(finalItems);
            removed = PlayerOriginals.Where(item => !remaining.Contains(item)).ToList();
            var inventory = PlayerInventory.GetAllItems();
            if (inventory.Capacity < finalItems.Count) inventory.Capacity = finalItems.Count;
            foreach (var staged in PlayerStage.GetAllItems())
            {
                if (Bindings.TryGetValue(staged, out var original))
                {
                    // Preserve live equipment references and unrelated durability/runtime changes.
                    original.m_stack = staged.m_stack; original.m_gridPos = staged.m_gridPos;
                }
            }
            inventory.Clear(); inventory.AddRange(finalItems);
        }
        internal void Notify()
        {
            if (notified) return;
            notified = true;
            foreach (var item in removed)
            {
                Effect(() => Owner.RemoveEquipAction(item));
                Effect(() => Owner.UnequipItem(item, false));
            }
            Effect(() => InventoryCodec.Changed(PlayerInventory));
        }
    }

    internal static bool Owns(Container? container) => HasSession && container && container == Chest;
    internal static Container? CurrentContainer => InventoryGui.instance ? AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer")(InventoryGui.instance) : null;
    internal static bool GuildUi => Owns(CurrentContainer);

    internal static bool Open(Container container, QuickAction action = QuickAction.None)
        => OpenCore(container, action, null, null);
    private static bool OpenCore(Container container, QuickAction action, Action<Inventory, Inventory>? transfer, TransferCompletion? completion)
    {
        if (Pending || Opening || HasSession || finishing || resetting) { Plugin.Message("The guild inventory is busy."); return false; }
        if (!Player.m_localPlayer || Player.m_localPlayer.IsDead() || !PrivateArea.CheckAccess(container.transform.position)) return false;
        var zdo = container.GetComponent<ZNetView>().GetZDO();
        if (zdo == null) return false;
        chestId = zdo.m_uid;
        opening = new OpenRequest {
            Chest = container, Player = Player.m_localPlayer, Inventory = Player.m_localPlayer.GetInventory(),
            Id = chestId, Sequence = ++openRequest, Started = Time.unscaledTime, Action = action,
            Transfer = transfer, Completion = completion
        };
        lastReply = Time.unscaledTime;
        try { Send(action == QuickAction.Automation ? Operation.OpenAutomation : Operation.Open, "", openRequest); }
        catch (Exception exception) { opening = null; Plugin.Error(exception); return false; }
        return true;
    }

    internal static bool Transfer(Container container, Action<Inventory, Inventory> action, Action<bool, string>? completed)
    {
        if (Pending || Opening || finishing || resetting || !Player.m_localPlayer || Player.m_localPlayer.IsDead() || Player.m_localPlayer.IsTeleporting()) return false;
        if (HasSession && !Owns(container)) return false;
        var completion = new TransferCompletion(completed, Plugin.Error);
        if (Owns(container)) { StageAutomation(action, completion); return true; }
        return OpenCore(container, QuickAction.Automation, action, completion);
    }

    private static void StageAutomation(Action<Inventory, Inventory> action, TransferCompletion completed)
    {
        try
        {
            if (!Player.m_localPlayer || Player.m_localPlayer.IsDead() || Player.m_localPlayer.IsTeleporting())
                throw new InvalidOperationException("The player is no longer available for this transfer.");
            var candidate = new Transaction { Completed = completed };
            action(candidate.PlayerStage, candidate.SharedStage);
            if (!Submit(candidate))
            {
                if (closeWanted) Close();
                completed.Complete(true, "No items transferred.");
            }
        }
        catch (Exception exception)
        {
            Plugin.Error(exception);
            if (Pending) return; // A sent request needs an acknowledgement, not a guessed outcome.
            if (closeWanted) Close();
            completed.Complete(false, exception.Message);
        }
    }

    private static ZPackage Header(Operation operation, string sessionToken, long requestSequence, ZDOID? target = null)
    {
        var package = new ZPackage(); package.Write((int)operation); package.Write(target ?? chestId);
        package.Write(sessionToken); package.Write(requestSequence); return package;
    }
    private static void Send(Operation operation, string sessionToken, long requestSequence, ZDOID? target = null) => Plugin.RequestRpc.SendPackage(Plugin.ServerId, Header(operation, sessionToken, requestSequence, target));
    private static void ReleaseGrant(ZDOID id, string receivedToken)
    {
        if (receivedToken.Length != 0 && receivedToken != token && (opening == null || opening.Id != id))
            Effect(() => Send(Operation.Close, receivedToken, 0, id));
    }
    private static bool Valid(OpenRequest request) => request.Chest && request.Player && request.Player == Player.m_localPlayer &&
        !request.Player.IsDead() && !request.Player.IsTeleporting() && request.Player.GetInventory() == request.Inventory;
    private static void CancelOpen(string reason)
    {
        var request = opening; opening = null;
        if (request == null) return;
        if (request.Completion != null) request.Completion.Complete(false, reason);
        else Plugin.Message(reason);
    }
    private static bool Effect(Action action)
    {
        try { action(); return true; }
        catch (Exception exception) { Plugin.Error(exception); return false; }
    }

    internal static IEnumerator Receive(long sender, ZPackage package)
    {
        try { Handle(sender, package); }
        catch (Exception exception) { Plugin.Error(exception); Plugin.Message("Guild Chest could not read the host response."); }
        yield break;
    }
    private static void Handle(long sender, ZPackage package)
    {
        if (!ZNet.instance || sender != Plugin.ServerId || package.Size() > InventoryCodec.MaxBytes + 4096) return;
        var operation = (Operation)package.ReadInt(); var id = package.ReadZDOID(); string receivedToken = package.ReadString(); long requestSequence = package.ReadLong();
        var result = (AccessResult)package.ReadInt(); long receivedRevision = package.ReadLong(); byte[] bytes = package.ReadByteArray();
        string reason = package.GetPos() < package.Size() ? package.ReadString() : result.ToString();
        if (operation == Operation.Peek) { StorageCompatibility.ReceiveSnapshot(id, requestSequence, result, receivedRevision, bytes); return; }
        if (operation == Operation.Open || operation == Operation.OpenAutomation)
        {
            if (opening == null || id != opening.Id || requestSequence != opening.Sequence ||
                operation != (opening.Action == QuickAction.Automation ? Operation.OpenAutomation : Operation.Open))
            {
                if (result == AccessResult.Accepted) ReleaseGrant(id, receivedToken);
                return;
            }
            var requested = opening; opening = null;
            if (!Valid(requested))
            {
                if (result == AccessResult.Accepted) ReleaseGrant(id, receivedToken);
                requested.Completion?.Complete(false, "The player or guild chest is no longer available.");
                return;
            }
            if (result != AccessResult.Accepted)
            {
                requested.Completion?.Complete(false, reason);
                Plugin.Message(result == AccessResult.Busy ? "Someone is using the guild inventory." : $"Guild Chest: {reason}"); return;
            }
            Inventory decoded;
            try { decoded = InventoryCodec.Read(bytes); }
            catch (Exception exception)
            {
                // A server may have an item mod that this client lacks. Release the
                // granted lease before reporting the failure instead of keeping it alive.
                ReleaseGrant(id, receivedToken);
                Reset(); requested.Completion?.Complete(false, exception.Message);
                Plugin.Message($"Guild Chest: {exception.Message}"); return;
            }
            chestId = id; Chest = requested.Chest; token = receivedToken; sequence = 0; revision = receivedRevision;
            View = decoded; lastReply = Time.unscaledTime; nextHeartbeat = lastReply + HeartbeatInterval;
            StorageCompatibility.Protect(View);
            AccessTools.FieldRefAccess<Container, Inventory>("m_inventory")(Chest) = View;
            if (requested.Action == QuickAction.Automation)
            {
                closeWanted = true;
                StageAutomation(requested.Transfer!, requested.Completion!);
            }
            else if (requested.Action == QuickAction.None)
            {
                if (!Effect(() => InventoryGui.instance.Show(Chest))) Close();
            }
            else { closeWanted = true; Bulk(requested.Action == QuickAction.Take); }
            return;
        }
        if (id != chestId) return;
        if (receivedToken != token || !HasSession) return;
        lastReply = Time.unscaledTime;
        if (operation == Operation.Commit)
        {
            if (transaction == null || requestSequence != transaction.Sequence) return;
            var finished = transaction;
            if (!finished.Lifecycle.Acknowledge(result == AccessResult.Accepted)) return;
            finished.ReplyRevision = receivedRevision; finished.ReplyInventory = bytes; finished.ReplyReason = reason;
            if (result != AccessResult.Accepted)
                Plugin.LogWarning($"Transfer {requestSequence} rejected by host: {result}. {reason}");
            Finish(finished);
        }
        else if (operation == Operation.Heartbeat && result != AccessResult.Accepted)
        {
            closeWanted = true;
            if (!Pending) Close();
        }
    }

    private static void Finish(Transaction finished)
    {
        if (finishing || transaction != finished) return;
        bool accepted = finished.Lifecycle.Phase == TransferPhase.Accepted || finished.Lifecycle.Phase == TransferPhase.Applied;
        finishing = true;
        try
        {
            if (accepted)
            {
                try { finished.Lifecycle.TryApply(finished.Apply); }
                catch (Exception exception)
                {
                    Plugin.Error(exception); nextRetry = Time.unscaledTime + RetryInterval;
                    return; // Retry preparation locally, never resend an accepted commit.
                }
                revision = finished.ReplyRevision;
                Applying = true;
                finished.Notify();
                if (!Effect(() => { if (View != null) InventoryCodec.Replace(View, InventoryCodec.Read(finished.ReplyInventory)); })) closeWanted = true;
                if (!Effect(() => StorageCompatibility.RefreshSnapshot(finished.ReplyInventory, finished.ReplyRevision))) closeWanted = true;
            }
            else
            {
                Effect(() => Plugin.Message($"Transfer rejected: {finished.ReplyReason} Your items have not moved."));
                closeWanted = true;
            }
            transaction = null;
            Effect(ClearDrag);
            if (deferredDeath && Player.m_localPlayer)
            {
                deferredDeath = false; Effect(() => Player.m_localPlayer.OnDeath()); closeWanted = true;
            }
            if (closeWanted || !Chest) Close();
            finished.Lifecycle.Finish();
        }
        finally { Applying = false; finishing = false; }
        finished.Completed?.Complete(accepted, finished.ReplyReason);
    }

    internal static void Close()
    {
        if (Pending) { closeWanted = true; return; }
        if (HasSession && ZNet.instance && ZRoutedRpc.instance != null) Effect(() => Send(Operation.Close, token, sequence));
        Reset();
    }
    internal static void Reset()
    {
        var request = opening; var unfinished = transaction;
        Chest = null; opening = null; View = null; token = ""; transaction = null; closeWanted = false; deferredDeath = false;
        resetting = true;
        try
        {
            request?.Completion?.Complete(false, "The guild inventory session ended before it opened.");
            unfinished?.Completed?.Complete(unfinished.Lifecycle.Phase == TransferPhase.Applied,
                "The session ended before transfer completion. A sent transfer may already have been accepted by the host.");
        }
        finally { resetting = false; }
    }
    internal static bool DeferDeath(Player player)
    {
        if (player != Player.m_localPlayer || !Pending) return false;
        deferredDeath = true; closeWanted = true; return true;
    }
    internal static void Tick()
    {
        if (!ZNet.instance || !ZNetScene.instance) return;
        float now = Time.unscaledTime;
        if (opening != null && !Valid(opening)) CancelOpen("The player or guild chest is no longer available.");
        else if (opening != null && now - opening.Started > OpenTimeout) CancelOpen("Guild Chest host did not respond.");
        if (!HasSession) return;
        if (now >= nextHeartbeat) { Effect(() => Send(Operation.Heartbeat, token, sequence)); nextHeartbeat = now + HeartbeatInterval; }
        if (Pending)
        {
            if (now >= nextRetry)
            {
                var current = transaction!; nextRetry = now + RetryInterval;
                if (current.Lifecycle.Phase == TransferPhase.Accepted || current.Lifecycle.Phase == TransferPhase.Applied) Finish(current);
                else Effect(() => Plugin.RequestRpc.SendPackage(Plugin.ServerId, new ZPackage(current.Request)));
            }
        }
        else if (!Chest || !Player.m_localPlayer || Player.m_localPlayer.IsDead() || now - lastReply > 35 ||
            Vector3.Distance(Chest.transform.position, Player.m_localPlayer.transform.position) > 4f) Close();
    }

    private static bool Submit(Transaction candidate)
    {
        foreach (var item in candidate.SharedStage.GetAllItems()) item.m_equipped = false;
        byte[] shared = InventoryCodec.Save(candidate.SharedStage);
        if (shared.SequenceEqual(InventoryCodec.Save(View!))) { Effect(ClearDrag); return false; }
        byte[] player = InventoryCodec.Save(candidate.PlayerStage);
        if (shared.Length > InventoryCodec.MaxBytes || player.Length > InventoryCodec.MaxBytes) throw new InvalidOperationException("This transfer exceeds the supported inventory size.");
        candidate.Sequence = ++sequence;
        var package = Header(Operation.Commit, token, candidate.Sequence); package.Write(revision);
        package.Write(candidate.PlayerStage.GetWidth()); package.Write(candidate.PlayerStage.GetHeight());
        package.Write(candidate.PlayerBefore); package.Write(shared); package.Write(player);
        candidate.Request = package.GetArray(); transaction = candidate; nextRetry = Time.unscaledTime + RetryInterval;
        Effect(ClearDrag); Effect(() => Plugin.RequestRpc.SendPackage(Plugin.ServerId, new ZPackage(candidate.Request)));
        return true;
    }
    internal static void Bulk(bool take)
    {
        if (!HasSession || Pending || !Player.m_localPlayer || Player.m_localPlayer.IsTeleporting()) return;
        var candidate = new Transaction();
        if (take) candidate.PlayerStage.MoveAll(candidate.SharedStage);
        else candidate.SharedStage.StackAll(candidate.PlayerStage);
        if (!Submit(candidate) && closeWanted) Close();
    }
    internal static void ClearDrag()
    {
        if (InventoryGui.instance) AccessTools.Method(typeof(InventoryGui), "SetupDragItem").Invoke(InventoryGui.instance, new object?[] { null, null, 1 });
    }

    /// <returns>Whether the vanilla selection handler should run.</returns>
    internal static bool Select(InventoryGui gui, InventoryGrid grid, ItemDrop.ItemData? item, Vector2i position, InventoryGrid.Modifier modifier)
    {
        if (Pending) return false;
        if (!GuildUi) return true;
        if (Player.m_localPlayer.IsTeleporting()) return false;
        var destination = grid.GetInventory();
        var dragInventory = AccessTools.FieldRefAccess<InventoryGui, Inventory>("m_dragInventory")(gui);
        var dragItem = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_dragItem")(gui);
        bool dragging = AccessTools.FieldRefAccess<InventoryGui, GameObject>("m_dragGo")(gui);
        if (modifier == InventoryGrid.Modifier.Drop && destination == View)
        {
            Plugin.Message("Move the item into your inventory before dropping it."); return false;
        }
        if (dragging && (dragInventory == View || destination == View))
        {
            if (dragItem == null || !dragInventory.ContainsItem(dragItem)) { ClearDrag(); return false; }
            if (dragInventory != destination && (dragItem.m_shared.m_questItem || item?.m_shared.m_questItem == true)) return false;
            var candidate = new Transaction();
            var from = dragInventory == View ? candidate.SharedStage : candidate.PlayerStage;
            var to = destination == View ? candidate.SharedStage : candidate.PlayerStage;
            var staged = from.GetItemAt(dragItem.m_gridPos.x, dragItem.m_gridPos.y);
            int amount = AccessTools.FieldRefAccess<InventoryGui, int>("m_dragAmount")(gui);
            ref var gridInventory = ref AccessTools.FieldRefAccess<InventoryGrid, Inventory>("m_inventory")(grid);
            var original = gridInventory;
            try { gridInventory = to; grid.DropItem(from, staged, amount, position); }
            finally { gridInventory = original; }
            Submit(candidate); return false;
        }
        if (modifier == InventoryGrid.Modifier.Move && item != null)
        {
            if (item.m_shared.m_questItem) return false;
            var candidate = new Transaction();
            var from = destination == View ? candidate.SharedStage : candidate.PlayerStage;
            var to = destination == View ? candidate.PlayerStage : candidate.SharedStage;
            var staged = from.GetItemAt(item.m_gridPos.x, item.m_gridPos.y);
            to.MoveItemToThis(from, staged); Submit(candidate); return false;
        }
        return true;
    }
}
