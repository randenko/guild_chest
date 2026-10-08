using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GuildChest;

internal static class AutoStoreAdapter
{
    internal const string PluginId = "Azumatt.AzuAutoStore";
    private sealed class Bindings
    {
        internal readonly MethodInfo Query, Store, Success, ClearPending, Ping, StoreAll, StoreSingle;
        internal readonly FieldInfo Pings;
        internal readonly Func<float> Range;
        internal readonly (MethodInfo Target, string Prefix)[] Hooks;
        internal Bindings(Type plugin)
        {
            var assembly = plugin.Assembly;
            var functions = StorageBindings.Type(assembly, "AzuAutoStore.Util.Functions");
            var boxes = StorageBindings.Type(assembly, "AzuAutoStore.Util.Boxes");
            var wrapper = StorageBindings.Type(assembly, "AzuAutoStore.Interfaces.VanillaContainers");
            var contract = StorageBindings.Type(assembly, "AzuAutoStore.Interfaces.IContainer");
            ContainerBindings.Validate(wrapper, false);
            Query = StorageBindings.NearbyQuery(boxes);
            Store = StorageBindings.Method(functions, "StoreToContainer", typeof(int), new[] { contract, typeof(ItemDrop.ItemData), typeof(Inventory) });
            Success = StorageBindings.Method(functions, "StoreSuccess", typeof(void), new[] { typeof(int) });
            ClearPending = StorageBindings.Method(functions, "ClearPendingSends", typeof(void), Type.EmptyTypes);
            Ping = StorageBindings.Method(functions, "PingContainer", typeof(void), new[] { typeof(GameObject) });
            StoreAll = StorageBindings.Method(wrapper, "TryStore", typeof(int), Type.EmptyTypes);
            StoreSingle = StorageBindings.Method(wrapper, "TryStoreThisItem", typeof(int), new[] { typeof(ItemDrop.ItemData), typeof(Inventory) });
            Pings = StorageBindings.Field(boxes, "ContainersToPing");
            if (!Pings.IsStatic || !typeof(IList).IsAssignableFrom(Pings.FieldType))
                throw new InvalidOperationException("Unsupported AutoStore ping collection.");
            Range = StorageBindings.Setting<float>(plugin, "PlayerRange");
            Hooks = new[] {
                (Store, nameof(StoreContainer)),
                (StorageBindings.Method(functions, "TryStore", typeof(bool), new[] { typeof(Container), typeof(ItemDrop.ItemData).MakeByRefType(), typeof(bool), typeof(bool) }), nameof(StoreDirect)),
                (StorageBindings.Method(functions, "TryStore", typeof(void), Type.EmptyTypes), nameof(Begin)),
                (StorageBindings.Method(functions, "TryStoreThisItem", typeof(void), new[] { typeof(ItemDrop.ItemData), typeof(Inventory) }), nameof(Begin))
            };
        }
    }
    private static Bindings? bindings;
    private static Bindings Api => bindings ?? throw new InvalidOperationException("AutoStore adapter is unavailable.");
    private static IList Pings => (IList)Api.Pings.GetValue(null);
    internal static float Range => Api.Range();
    internal static void Install(Harmony harmony, Type plugin)
    {
        var resolved = new Bindings(plugin);
        StorageBindings.Patch(harmony, typeof(AutoStoreAdapter), resolved.Hooks);
        bindings = resolved;
    }
    internal static List<object> Nearby(Player player) => ((IEnumerable)Api.Query.Invoke(null, new object[] { player, Range })).Cast<object>().ToList();
    internal static int Store(object container, ItemDrop.ItemData? item, Inventory? inventory) => (int)Api.Store.Invoke(null, new object?[] { container, item, inventory });
    internal static void Success(int count) => Api.Success.Invoke(null, new object[] { count });
    internal static void ClearPending() { Pings.Clear(); Api.ClearPending.Invoke(null, null); }
    private static bool Begin(object[] __args) => AutoStoreBatch.Begin(__args);
    private static bool StoreDirect(Container nearbyContainer, ref bool __result)
    {
        if (!Plugin.IsGuild(nearbyContainer) || nearbyContainer == InventoryAccess.ScopedChest) return true;
        // Ground automation has no player transaction to acknowledge.
        __result = false; return false;
    }
    private static bool StoreContainer(object container, ItemDrop.ItemData? singleItem, Inventory? inv, ref int __result)
    {
        if (!Plugin.IsGuild(ContainerBindings.ContainerFor(container))) return !Client.Pending;
        __result = 0; StoreGuild(container, singleItem, inv, null); return false;
    }
    internal static bool StoreGuild(object container, ItemDrop.ItemData? singleItem, Inventory? inv, Action? completed)
    {
        var chest = ContainerBindings.ContainerFor(container);
        if (bindings == null || Client.Pending || Client.Opening) return false;
        int stored = 0;
        return GuildChestStorage.TryTransfer(chest!, (player, shared) =>
        {
            // Keep upstream favorites, hotbar, equipment, YAML and stacking rules.
            ItemDrop.ItemData? staged = null;
            if (singleItem != null)
            {
                var real = Player.m_localPlayer.GetInventory();
                if (inv != real || !real.ContainsItem(singleItem)) return;
                staged = player.GetItemAt(singleItem.m_gridPos.x, singleItem.m_gridPos.y);
            }
            using var scope = new InventoryAccess.StagingScope(Player.m_localPlayer, chest!, player, shared);
            try
            {
                stored = (int)(singleItem == null ? Api.StoreAll.Invoke(container, null) : Api.StoreSingle.Invoke(container, new object?[] { staged, player }));
            }
            finally { Pings.Remove(container); } // Play this destination's effect once, after ACK.
        }, (accepted, reason) =>
        {
            try
            {
                if (accepted && stored > 0)
                {
                    Plugin.Message($"Stored {stored} items in guild storage.");
                    if (chest)
                    {
                        Api.Ping.Invoke(null, new object[] { chest!.gameObject });
                        if (InventoryGui.instance)
                            AccessTools.FieldRefAccess<InventoryGui, EffectList>("m_moveItemEffects")(InventoryGui.instance)
                                .Create(chest!.transform.position, Quaternion.identity);
                    }
                }
                else if (!accepted) Plugin.Message($"Guild auto-store failed: {reason}");
            }
            finally { completed?.Invoke(); }
        });
    }
}
