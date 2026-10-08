using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuildChest;

internal static class CraftyBoxesAdapter
{
    internal const string PluginId = "Azumatt.AzuCraftyBoxes";
    private sealed class Bindings
    {
        internal readonly MethodInfo Query, ShouldPrevent, CanPull, Invalidate;
        internal readonly FieldInfo Totals, ContainersHavingAny;
        internal readonly Func<float> Range;
        internal readonly Func<int> LeaveOne;
        internal readonly (MethodInfo Target, string Prefix)[] Hooks;
        internal Bindings(Type plugin)
        {
            var assembly = plugin.Assembly;
            var boxes = StorageBindings.Type(assembly, "AzuCraftyBoxes.Util.Functions.Boxes");
            var misc = StorageBindings.Type(assembly, "AzuCraftyBoxes.Util.Functions.MiscFunctions");
            var bank = StorageBindings.Type(assembly, "AzuCraftyBoxes.Util.Functions.UiItemBank");
            var wrapper = StorageBindings.Type(assembly, "AzuCraftyBoxes.IContainers.VanillaContainer");
            var contract = StorageBindings.Type(assembly, "AzuCraftyBoxes.IContainers.IContainer");
            ContainerBindings.Validate(wrapper, true);
            Query = StorageBindings.NearbyQuery(boxes);
            ShouldPrevent = StorageBindings.Method(misc, "ShouldPrevent", typeof(bool), Type.EmptyTypes);
            CanPull = StorageBindings.Method(boxes, "CanItemBePulled", typeof(bool), new[] { typeof(string), typeof(string), typeof(string) });
            Invalidate = StorageBindings.Method(boxes, "InvalidateCounts", typeof(void), Type.EmptyTypes);
            Totals = StorageBindings.Field(bank, "_totals");
            ContainersHavingAny = StorageBindings.Field(bank, "_containersHavingAny");
            foreach (var field in new[] { Totals, ContainersHavingAny })
                if (!field.IsStatic || !typeof(IDictionary).IsAssignableFrom(field.FieldType))
                    throw new InvalidOperationException("Unsupported CraftyBoxes UI count cache.");
            Range = StorageBindings.Setting<float>(plugin, "mRange");
            LeaveOne = StorageBindings.Setting<int>(plugin, "leaveOne");
            Hooks = new[] {
                (StorageBindings.Method(wrapper, "ItemCount", typeof(int), new[] { typeof(string) }), nameof(Count)),
                (StorageBindings.Method(wrapper, "GetInventory", typeof(Inventory), Type.EmptyTypes), nameof(GetInventory)),
                (StorageBindings.Method(wrapper, "ProcessContainerInventory", typeof(int), new[] { typeof(string), typeof(int), typeof(int) }), nameof(Consume)),
                (StorageBindings.Method(wrapper, "RemoveItem", typeof(void), new[] { typeof(string), typeof(int) }), nameof(Remove)),
                (StorageBindings.Method(boxes, "MemoUsable", typeof(bool), new[] { typeof(List<>).MakeGenericType(contract) }), nameof(Memo))
            };
        }
    }
    private static Bindings? bindings;
    internal static bool Installed => bindings != null;
    private static Bindings Api => bindings ?? throw new InvalidOperationException("CraftyBoxes adapter is unavailable.");
    internal static void Install(Harmony harmony, Type plugin)
    {
        var resolved = new Bindings(plugin);
        StorageBindings.Patch(harmony, typeof(CraftyBoxesAdapter), resolved.Hooks);
        bindings = resolved;
    }
    internal static List<object> Nearby()
    {
        if (!Installed || !Player.m_localPlayer || (bool)Api.ShouldPrevent.Invoke(null, null)) return new();
        return ((IEnumerable)Api.Query.Invoke(null, new object[] { Player.m_localPlayer, Api.Range() })).Cast<object>().ToList();
    }
    internal static bool LeaveOne => Api.LeaveOne() != 0;
    internal static bool Pullable(object wrapper, string prefab) =>
        (bool)Api.CanPull.Invoke(null, new[] { ContainerBindings.PrefabFor(wrapper), prefab, "" });
    internal static void InvalidateCounts()
    {
        if (!Installed) return;
        Api.Invalidate.Invoke(null, null);
        // The UI bank caches for an entire frame. Clear it at scope boundaries
        // and ACK so preview totals never fund an unadapted resource consumer.
        (Api.Totals.GetValue(null) as IDictionary)?.Clear();
        (Api.ContainersHavingAny.GetValue(null) as IDictionary)?.Clear();
    }
    private static bool Count(object __instance, string name, ref int __result)
    {
        var chest = ContainerBindings.ContainerFor(__instance);
        if (!Plugin.IsGuild(chest)) return true;
        __result = StoragePreview.For(chest)?.CountItems(name) ?? 0; return false;
    }
    private static bool GetInventory(object __instance, ref Inventory __result)
    {
        var chest = ContainerBindings.ContainerFor(__instance);
        if (!Plugin.IsGuild(chest)) return true;
        var preview = StoragePreview.For(chest);
        if (preview == null) __result = InventoryAccess.InventoryFor(chest!);
        else { __result = InventoryCodec.Clone(preview); InventoryAccess.Protect(__result); }
        return false;
    }
    private static bool Consume(object __instance, int totalAmount, ref int __result)
    {
        if (!Plugin.IsGuild(ContainerBindings.ContainerFor(__instance))) return true;
        __result = totalAmount; return false;
    }
    private static bool Remove(object __instance) => !Plugin.IsGuild(ContainerBindings.ContainerFor(__instance));
    private static bool Memo(ref bool __result)
    {
        if (StorageScopes.PreviewDepth > 0 && StorageScopes.ConsumptionDepth == 0) return true;
        __result = false; return false;
    }
}
