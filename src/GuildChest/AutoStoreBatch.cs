using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GuildChest;

// Keep AutoStore's destination order and rules, yielding only for a guild RPC.
// Resume in Update so synchronous callbacks never recurse into the next write.
internal static class AutoStoreBatch
{
    private sealed class Batch
    {
        internal Player Player = null!;
        internal Inventory Inventory = null!;
        internal ItemDrop.ItemData? Item;
        internal List<object> Destinations = null!;
        internal int Index, OrdinaryStored;
        internal bool Waiting, UsedGuild;
    }
    private static Batch? active;
    internal static void Reset() { active = null; }
    private static bool Available(object wrapper, Player player, bool guild)
    {
        var obj = ContainerBindings.ObjectFor(wrapper);
        if (!obj || Vector3.Distance(obj!.transform.position, player.transform.position) > (guild ? Math.Min(AutoStoreAdapter.Range, Plugin.AutomationRange.Value) : AutoStoreAdapter.Range)) return false;
        var view = obj.GetComponent<ZNetView>();
        return (!view || view.IsValid()) && PrivateArea.CheckAccess(obj.transform.position, 0f, false, true);
    }
    internal static bool Begin(object[] __args)
    {
        if (active != null || Client.Pending || Client.Opening) return false;
        var player = Player.m_localPlayer;
        if (!player || player.IsDead() || player.IsTeleporting()) return true;
        var inventory = player.GetInventory();
        var item = __args.Length == 0 ? null : __args[0] as ItemDrop.ItemData;
        if (__args.Length != 0 && (item == null || __args[1] != inventory || !inventory.ContainsItem(item))) return false;
        try
        {
            var destinations = AutoStoreAdapter.Nearby(player);
            if (!destinations.Any(destination => Plugin.IsGuild(ContainerBindings.ContainerFor(destination)))) return true;
            AutoStoreAdapter.ClearPending();
            active = new Batch { Player = player, Inventory = inventory, Item = item, Destinations = destinations };
            Tick();
        }
        catch (Exception exception) { Reset(); Plugin.Error(exception); }
        return false;
    }
    private static void Flush(Batch batch)
    {
        int total = batch.OrdinaryStored; batch.OrdinaryStored = 0;
        AutoStoreAdapter.Success(total);
    }
    internal static void Tick()
    {
        var batch = active;
        if (batch == null || batch.Waiting || Client.Pending || Client.Opening) return;
        try
        {
            if (!batch.Player || batch.Player != Player.m_localPlayer || batch.Player.IsDead() || batch.Player.IsTeleporting() ||
                batch.Player.GetInventory() != batch.Inventory || (batch.Item != null && !batch.Inventory.ContainsItem(batch.Item)))
            { Flush(batch); Reset(); return; }
            while (batch.Index < batch.Destinations.Count)
            {
                var destination = batch.Destinations[batch.Index++];
                bool guild = Plugin.IsGuild(ContainerBindings.ContainerFor(destination));
                if (guild && batch.UsedGuild || !Available(destination, batch.Player, guild)) continue;
                if (!guild)
                {
                    batch.OrdinaryStored += AutoStoreAdapter.Store(destination, batch.Item, batch.Item == null ? null : batch.Inventory);
                    if (batch.Item != null && !batch.Inventory.ContainsItem(batch.Item)) break;
                    continue;
                }
                Flush(batch); batch.UsedGuild = true; batch.Waiting = true;
                bool started = AutoStoreAdapter.StoreGuild(destination, batch.Item, batch.Item == null ? null : batch.Inventory, () =>
                {
                    if (active == batch) batch.Waiting = false;
                });
                if (!started) batch.Waiting = false;
                return;
            }
            Flush(batch); Reset();
        }
        catch (Exception exception)
        {
            Reset(); AutoStoreAdapter.ClearPending(); Plugin.Error(exception);
        }
    }
}
