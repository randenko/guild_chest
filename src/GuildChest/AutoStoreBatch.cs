using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
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
    private static Type? plugin, boxes, functions;
    private static MethodInfo? query, store, success, clearPending;
    private static IList? Pings => boxes == null ? null : AccessTools.Field(boxes, "ContainersToPing")?.GetValue(null) as IList;
    private static float Range
    {
        get
        {
            var setting = AccessTools.Field(plugin, "PlayerRange").GetValue(null);
            return (float)setting.GetType().GetProperty("Value")!.GetValue(setting);
        }
    }
    internal static void Install(Harmony harmony, Type pluginType, Type functionsType)
    {
        plugin = pluginType; functions = functionsType;
        boxes = plugin.Assembly.GetType("AzuAutoStore.Util.Boxes");
        query = AccessTools.Method(boxes, "GetNearbyContainers")?.MakeGenericMethod(typeof(Player));
        store = AccessTools.Method(functions, "StoreToContainer");
        success = AccessTools.Method(functions, "StoreSuccess");
        clearPending = AccessTools.Method(functions, "ClearPendingSends");
        if (query == null || store == null || success == null || clearPending == null || AccessTools.Field(plugin, "PlayerRange") == null)
        { Plugin.LogWarning("AutoStore batch hooks unavailable; ordinary entry points remain unchanged."); return; }
        foreach (var entry in new[] { AccessTools.Method(functions, "TryStore", Type.EmptyTypes), AccessTools.Method(functions, "TryStoreThisItem") })
            if (entry != null) harmony.Patch(entry, prefix: new HarmonyMethod(typeof(AutoStoreBatch), nameof(Begin)) { priority = Priority.First });
    }
    internal static void ForgetPing(object container) => Pings?.Remove(container);
    internal static void Reset() { active = null; }
    private static GameObject? ObjectFor(object wrapper) => AccessTools.Property(wrapper.GetType(), "gameObject")?.GetValue(wrapper, null) as GameObject;
    private static bool Available(object wrapper, Player player, bool guild)
    {
        var obj = ObjectFor(wrapper);
        if (!obj || Vector3.Distance(obj!.transform.position, player.transform.position) > (guild ? Math.Min(Range, Plugin.AutomationRange.Value) : Range)) return false;
        var view = obj.GetComponent<ZNetView>();
        return (!view || view.IsValid()) && PrivateArea.CheckAccess(obj.transform.position, 0f, false, true);
    }
    private static bool Begin(object[] __args)
    {
        if (active != null || Client.Pending || Client.Opening) return false;
        var player = Player.m_localPlayer;
        if (!player || player.IsDead() || player.IsTeleporting()) return true;
        var inventory = player.GetInventory();
        var item = __args.Length == 0 ? null : __args[0] as ItemDrop.ItemData;
        if (__args.Length != 0 && (item == null || __args[1] != inventory || !inventory.ContainsItem(item))) return false;
        try
        {
            var destinations = ((IEnumerable)query!.Invoke(null, new object[] { player, Range })).Cast<object>().ToList();
            if (!destinations.Any(destination => Plugin.IsGuild(StorageCompatibility.WrappedContainer(destination)))) return true;
            Pings?.Clear(); clearPending!.Invoke(null, null);
            active = new Batch { Player = player, Inventory = inventory, Item = item, Destinations = destinations };
            Tick();
        }
        catch (Exception exception) { Reset(); Plugin.Error(exception); }
        return false;
    }
    private static void Flush(Batch batch)
    {
        int total = batch.OrdinaryStored; batch.OrdinaryStored = 0;
        success!.Invoke(null, new object[] { total });
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
                bool guild = Plugin.IsGuild(StorageCompatibility.WrappedContainer(destination));
                if (guild && batch.UsedGuild || !Available(destination, batch.Player, guild)) continue;
                if (!guild)
                {
                    batch.OrdinaryStored += (int)store!.Invoke(null, new object?[] { destination, batch.Item, batch.Item == null ? null : batch.Inventory });
                    if (batch.Item != null && !batch.Inventory.ContainsItem(batch.Item)) break;
                    continue;
                }
                Flush(batch); batch.UsedGuild = true; batch.Waiting = true;
                bool started = StorageCompatibility.StoreGuild(destination, batch.Item, batch.Item == null ? null : batch.Inventory, () =>
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
            Reset(); Pings?.Clear(); clearPending?.Invoke(null, null); Plugin.Error(exception);
        }
    }
}
