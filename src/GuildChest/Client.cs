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
    private static Container? opening;
    private static long openRequest;
    private static ZDOID chestId;
    private static string token = "";
    private static long revision, sequence;
    private static float nextHeartbeat, nextRetry, lastReply;
    private static bool closeWanted;
    private static bool deferredDeath;
    private static QuickAction quickAction;
    private static Transaction? transaction;
    private static Action<Inventory, Inventory>? automationAction;
    private static Action<bool, string>? automationCompleted;
    internal static bool Applying;
    internal static bool Opening => opening;

    internal sealed class Transaction
    {
        internal readonly Inventory PlayerStage;
        internal readonly List<ItemDrop.ItemData> PlayerOriginals;
        internal readonly Dictionary<ItemDrop.ItemData, ItemDrop.ItemData> Bindings = new();
        internal readonly Inventory SharedStage;
        internal readonly byte[] PlayerBefore;
        internal byte[] Request = Array.Empty<byte>();
        internal long Sequence;
        internal Action<bool, string>? Completed;
        internal Transaction()
        {
            var player = Player.m_localPlayer.GetInventory();
            PlayerBefore = InventoryCodec.Save(player);
            PlayerOriginals = player.GetAllItems().ToList();
            PlayerStage = InventoryCodec.Clone(player); SharedStage = InventoryCodec.Clone(View!);
            for (int i = 0; i < PlayerOriginals.Count; i++) Bindings.Add(PlayerStage.GetAllItems()[i], PlayerOriginals[i]);
        }
        internal void Apply()
        {
            var player = Player.m_localPlayer;
            var inventory = player.GetInventory();
            var remaining = new HashSet<ItemDrop.ItemData>(PlayerStage.GetAllItems().Where(Bindings.ContainsKey).Select(item => Bindings[item]));
            foreach (var item in PlayerOriginals)
            {
                if (remaining.Contains(item)) continue;
                player.RemoveEquipAction(item); player.UnequipItem(item, false);
                inventory.GetAllItems().Remove(item);
            }
            foreach (var staged in PlayerStage.GetAllItems())
            {
                if (Bindings.TryGetValue(staged, out var original))
                {
                    // Preserve live equipment references and unrelated durability/runtime changes.
                    original.m_stack = staged.m_stack; original.m_gridPos = staged.m_gridPos;
                }
                else
                {
                    var item = staged.Clone(); item.m_equipped = false; inventory.GetAllItems().Add(item);
                }
            }
            InventoryCodec.Changed(inventory);
        }
    }

    internal static bool Owns(Container? container) => HasSession && container && container == Chest;
    internal static Container? CurrentContainer => InventoryGui.instance ? AccessTools.FieldRefAccess<InventoryGui, Container>("m_currentContainer")(InventoryGui.instance) : null;
    internal static bool GuildUi => Owns(CurrentContainer);

    internal static bool Open(Container container, QuickAction action = QuickAction.None)
    {
        if (Pending || opening || HasSession) { Plugin.Message("The guild inventory is busy."); return false; }
        if (!Player.m_localPlayer || Player.m_localPlayer.IsDead() || !PrivateArea.CheckAccess(container.transform.position)) return false;
        var zdo = container.GetComponent<ZNetView>().GetZDO();
        if (zdo == null) return false;
        opening = container; chestId = zdo.m_uid; quickAction = action; openRequest++;
        lastReply = Time.unscaledTime;
        Send(action == QuickAction.Automation ? Operation.OpenAutomation : Operation.Open, "", openRequest);
        return true;
    }

    internal static bool Transfer(Container container, Action<Inventory, Inventory> action, Action<bool, string>? completed)
    {
        if (Pending || opening || !Player.m_localPlayer || Player.m_localPlayer.IsDead() || Player.m_localPlayer.IsTeleporting()) return false;
        if (HasSession && !Owns(container)) return false;
        if (Owns(container)) { StageAutomation(action, completed); return true; }
        automationAction = action; automationCompleted = completed;
        // Local-host RPCs can complete synchronously and clear opening before Open
        // returns. Whether the request was sent is independent of that field.
        if (Open(container, QuickAction.Automation)) return true;
        automationAction = null; automationCompleted = null; return false;
    }

    private static void StageAutomation(Action<Inventory, Inventory> action, Action<bool, string>? completed)
    {
        try
        {
            if (!Player.m_localPlayer || Player.m_localPlayer.IsDead() || Player.m_localPlayer.IsTeleporting())
                throw new InvalidOperationException("The player is no longer available for this transfer.");
            var candidate = new Transaction { Completed = completed };
            action(candidate.PlayerStage, candidate.SharedStage);
            if (!Submit(candidate)) completed?.Invoke(true, "No items transferred.");
        }
        catch (Exception exception)
        {
            Plugin.Error(exception); completed?.Invoke(false, exception.Message);
        }
    }

    private static ZPackage Header(Operation operation, string sessionToken, long requestSequence)
    {
        var package = new ZPackage(); package.Write((int)operation); package.Write(chestId);
        package.Write(sessionToken); package.Write(requestSequence); return package;
    }
    private static void Send(Operation operation, string sessionToken, long requestSequence) => Plugin.RequestRpc.SendPackage(Plugin.ServerId, Header(operation, sessionToken, requestSequence));

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
        if (id != chestId) return;
        if (operation == Operation.Open || operation == Operation.OpenAutomation)
        {
            if (!opening || requestSequence != openRequest) return;
            var requested = opening; opening = null;
            if (result != AccessResult.Accepted)
            {
                var failed = automationCompleted; automationAction = null; automationCompleted = null;
                failed?.Invoke(false, reason);
                Plugin.Message(result == AccessResult.Busy ? "Someone is using the guild inventory." : $"Guild Chest: {reason}"); return;
            }
            Inventory decoded;
            try { decoded = InventoryCodec.Read(bytes); }
            catch (Exception exception)
            {
                // A server may have an item mod that this client lacks. Release the
                // granted lease before reporting the failure instead of keeping it alive.
                Send(Operation.Close, receivedToken, 0);
                var failed = automationCompleted; Reset(); failed?.Invoke(false, exception.Message);
                Plugin.Message($"Guild Chest: {exception.Message}"); return;
            }
            Chest = requested; token = receivedToken; sequence = 0; revision = receivedRevision;
            View = decoded; lastReply = Time.unscaledTime; nextHeartbeat = lastReply + 5;
            StorageCompatibility.Protect(View);
            AccessTools.FieldRefAccess<Container, Inventory>("m_inventory")(requested) = View;
            if (quickAction == QuickAction.Automation)
            {
                var action = automationAction!; var completed = automationCompleted;
                automationAction = null; automationCompleted = null;
                StageAutomation(action, completed); Close();
            }
            else if (quickAction == QuickAction.None) InventoryGui.instance.Show(requested);
            else { Bulk(quickAction == QuickAction.Take); Close(); }
            return;
        }
        if (receivedToken != token || !HasSession) return;
        lastReply = Time.unscaledTime;
        if (operation == Operation.Commit)
        {
            if (transaction == null || requestSequence != transaction.Sequence) return;
            var finished = transaction;
            if (result == AccessResult.Accepted)
            {
                try { Applying = true; finished.Apply(); }
                finally { Applying = false; }
                revision = receivedRevision;
                if (View != null) InventoryCodec.Replace(View, InventoryCodec.Read(bytes));
                StorageCompatibility.RefreshSnapshot(bytes, receivedRevision);
            }
            else
            {
                Plugin.LogWarning($"Transfer {requestSequence} rejected by host: {result}. {reason}");
                Plugin.Message($"Transfer rejected ({result}): {reason} Your items have not moved."); closeWanted = true;
            }
            transaction = null;
            ClearDrag();
            if (deferredDeath && Player.m_localPlayer)
            {
                deferredDeath = false; Player.m_localPlayer.OnDeath(); closeWanted = true;
            }
            if (closeWanted || !Chest) Close();
            finished.Completed?.Invoke(result == AccessResult.Accepted, reason);
        }
        else if (operation == Operation.Heartbeat && result != AccessResult.Accepted)
        {
            closeWanted = true;
            if (!Pending) Close();
        }
    }

    internal static void Close()
    {
        if (Pending) { closeWanted = true; return; }
        if (HasSession && ZNet.instance && ZRoutedRpc.instance != null) Send(Operation.Close, token, sequence);
        Reset();
    }
    internal static void Reset()
    {
        Chest = null; opening = null; View = null; token = ""; transaction = null; closeWanted = false; deferredDeath = false; quickAction = QuickAction.None;
        automationAction = null; automationCompleted = null;
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
        if (opening && now - lastReply > 30)
        {
            opening = null; var failed = automationCompleted; automationAction = null; automationCompleted = null;
            failed?.Invoke(false, "Guild Chest host did not respond."); Plugin.Message("Guild Chest host did not respond.");
        }
        if (!HasSession) return;
        if (now >= nextHeartbeat) { Send(Operation.Heartbeat, token, sequence); nextHeartbeat = now + 5; }
        if (Pending)
        {
            if (now >= nextRetry) { Plugin.RequestRpc.SendPackage(Plugin.ServerId, new ZPackage(transaction!.Request)); nextRetry = now + 2; }
        }
        else if (!Chest || !Player.m_localPlayer || Player.m_localPlayer.IsDead() || now - lastReply > 35 ||
            Vector3.Distance(Chest.transform.position, Player.m_localPlayer.transform.position) > 4f) Close();
    }

    private static bool Submit(Transaction candidate)
    {
        foreach (var item in candidate.SharedStage.GetAllItems()) item.m_equipped = false;
        byte[] shared = InventoryCodec.Save(candidate.SharedStage);
        if (shared.SequenceEqual(InventoryCodec.Save(View!))) { ClearDrag(); return false; }
        byte[] player = InventoryCodec.Save(candidate.PlayerStage);
        if (shared.Length > InventoryCodec.MaxBytes || player.Length > InventoryCodec.MaxBytes) throw new InvalidOperationException("This transfer exceeds the supported inventory size.");
        candidate.Sequence = ++sequence;
        var package = Header(Operation.Commit, token, candidate.Sequence); package.Write(revision);
        package.Write(candidate.PlayerStage.GetWidth()); package.Write(candidate.PlayerStage.GetHeight());
        package.Write(candidate.PlayerBefore); package.Write(shared); package.Write(player);
        candidate.Request = package.GetArray(); transaction = candidate; nextRetry = Time.unscaledTime + 2;
        ClearDrag(); Plugin.RequestRpc.SendPackage(Plugin.ServerId, new ZPackage(candidate.Request));
        return true;
    }
    internal static void Bulk(bool take)
    {
        if (!HasSession || Pending || !Player.m_localPlayer || Player.m_localPlayer.IsTeleporting()) return;
        var candidate = new Transaction();
        if (take) candidate.PlayerStage.MoveAll(candidate.SharedStage);
        else candidate.SharedStage.StackAll(candidate.PlayerStage);
        Submit(candidate);
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
