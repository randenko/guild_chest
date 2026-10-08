using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuildChest;

[HarmonyPatch(typeof(Container), "Awake")]
internal static class GuildInventoryRegistrationPatch
{
    private static void Postfix(Container __instance) => StorageCompatibility.Track(__instance);
}

[HarmonyPatch(typeof(Container), nameof(Container.GetInventory))]
internal static class GuildInventoryAccessPatch
{
    private static bool Prefix(Container __instance, ref Inventory __result)
    {
        if (!Plugin.IsGuild(__instance)) return true;
        __result = StorageCompatibility.InventoryFor(__instance); return false;
    }
}

[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.IsOwner))]
internal static class AutomationOwnerPatch
{
    private static bool Prefix(ZNetView __instance, ref bool __result)
    {
        if (!StorageCompatibility.ScopedChest || StorageCompatibility.ScopedChest!.GetComponent<ZNetView>() != __instance) return true;
        __result = true; return false;
    }
}

[HarmonyPatch]
internal static class GuildUiInventoryScopePatch
{
    private static IEnumerable<MethodBase> TargetMethods() => new[] { "Show", "UpdateContainer", "UpdateContainerWeight" }
        .Select(name => AccessTools.Method(typeof(InventoryGui), name));
    private static void Prefix(ref bool __state) { __state = true; StorageCompatibility.UiDepth++; }
    private static void Finalizer(bool __state) { if (__state) StorageCompatibility.UiDepth--; }
}

[HarmonyPatch]
internal static class CraftyPreviewScopePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in typeof(Player).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (method.Name == "HaveRequirements" || method.Name == "HaveRequirementItems") yield return method;
        yield return AccessTools.Method(typeof(InventoryGui), "SetupRequirement");
        yield return AccessTools.Method(typeof(InventoryGui), "SetupRequirementList");
        yield return AccessTools.Method(typeof(Hud), "SetupPieceInfo");
    }
    [HarmonyPriority(Priority.First)]
    private static void Prefix(ref bool __state)
    {
        __state = true;
        if (StorageCompatibility.PreviewDepth++ == 0) StorageCompatibility.InvalidateCounts();
    }
    private static void Finalizer(bool __state)
    {
        if (__state && --StorageCompatibility.PreviewDepth == 0) StorageCompatibility.InvalidateCounts();
    }
}

[HarmonyPatch(typeof(InventoryGui), "DoCrafting")]
internal static class GuildCraftingSupplyPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(InventoryGui __instance, Player player, ref bool __state)
    {
        if (Client.Pending || Client.Opening) return false;
        var recipe = AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe")(__instance);
        var upgrade = AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem")(__instance);
        bool multi = AccessTools.FieldRefAccess<InventoryGui, bool>("m_multiCrafting")(__instance);
        int amount = multi ? AccessTools.FieldRefAccess<InventoryGui, int>("m_multiCraftAmount")(__instance) : 1;
        var station = player.GetCurrentCraftingStation();
        if (recipe && !player.NoCostCheat() && !ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoCraftCost) &&
            StorageCompatibility.SupplyCrafting(recipe, upgrade == null ? 1 : upgrade.m_quality + 1, amount, () =>
            {
                if (!__instance || !player || player.IsDead() || player.IsTeleporting() || player.GetCurrentCraftingStation() != station) return;
                if (AccessTools.FieldRefAccess<InventoryGui, Recipe>("m_craftRecipe")(__instance) != recipe ||
                    AccessTools.FieldRefAccess<InventoryGui, ItemDrop.ItemData>("m_craftUpgradeItem")(__instance) != upgrade ||
                    AccessTools.FieldRefAccess<InventoryGui, bool>("m_multiCrafting")(__instance) != multi ||
                    (multi && AccessTools.FieldRefAccess<InventoryGui, int>("m_multiCraftAmount")(__instance) != amount)) return;
                AccessTools.Method(typeof(InventoryGui), "DoCrafting").Invoke(__instance, new object[] { player });
            })) return false;
        __state = true; StorageCompatibility.ConsumptionDepth++; StorageCompatibility.InvalidateCounts(); return true;
    }
    private static void Finalizer(bool __state) { if (__state) { StorageCompatibility.ConsumptionDepth--; StorageCompatibility.InvalidateCounts(); } }
}

[HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
internal static class GuildBuildingSupplyPatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Player __instance, Piece piece, ref bool __result)
    {
        if (__instance != Player.m_localPlayer) return true;
        bool run = BuildingContinuation.TryPlace(__instance, piece);
        if (!run) __result = false;
        return run;
    }
}

[HarmonyPatch(typeof(Player), "UpdatePlacement")]
internal static class GuildBuildingResumePatch
{
    private static void Prefix(Player __instance, bool takeInput) => BuildingContinuation.BeforeUpdate(__instance, takeInput);
    private static void Finalizer(Player __instance) => BuildingContinuation.AfterUpdate(__instance);
}

[HarmonyPatch(typeof(Player), nameof(Player.ConsumeResources))]
internal static class GuildConsumptionScopePatch
{
    private static void Prefix(ref bool __state) { __state = true; StorageCompatibility.ConsumptionDepth++; StorageCompatibility.InvalidateCounts(); }
    private static void Finalizer(bool __state) { if (__state) { StorageCompatibility.ConsumptionDepth--; StorageCompatibility.InvalidateCounts(); } }
}

// Guard native inventory APIs too: third-party writers must use a staged transfer.
// Methods returning ItemData, bool, int and void need distinct Harmony signatures.
internal static class GuardedInventoryMethods
{
    internal static bool Blocked(Inventory inventory, object[] arguments) => StorageCompatibility.Blocked(inventory) ||
        arguments.OfType<Inventory>().Any(StorageCompatibility.Blocked);
    internal static IEnumerable<MethodBase> WithReturn(Type result) => typeof(Inventory)
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(method => method.ReturnType == result && new[] { "AddItem", "CanAddItem", "MoveAll", "StackAll", "MoveItemToThis", "RemoveItem", "RemoveOneItem", "RemoveAll", "RemoveUnequipped", "Load", "MoveInventoryToGrave", "HaveEmptySlot" }.Contains(method.Name));
}
[HarmonyPatch]
internal static class GuildInventoryBoolGuard
{
    private static IEnumerable<MethodBase> TargetMethods() => GuardedInventoryMethods.WithReturn(typeof(bool));
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Inventory __instance, object[] __args, ref bool __result)
    { if (!GuardedInventoryMethods.Blocked(__instance, __args)) return true; __result = false; return false; }
}
[HarmonyPatch]
internal static class GuildInventoryIntGuard
{
    private static IEnumerable<MethodBase> TargetMethods() => GuardedInventoryMethods.WithReturn(typeof(int));
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Inventory __instance, object[] __args, ref int __result)
    { if (!GuardedInventoryMethods.Blocked(__instance, __args)) return true; __result = 0; return false; }
}
[HarmonyPatch]
internal static class GuildInventoryItemGuard
{
    private static IEnumerable<MethodBase> TargetMethods() => GuardedInventoryMethods.WithReturn(typeof(ItemDrop.ItemData));
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Inventory __instance, object[] __args, ref ItemDrop.ItemData? __result)
    { if (!GuardedInventoryMethods.Blocked(__instance, __args)) return true; __result = null; return false; }
}
[HarmonyPatch]
internal static class GuildInventoryVoidGuard
{
    private static IEnumerable<MethodBase> TargetMethods() => GuardedInventoryMethods.WithReturn(typeof(void));
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Inventory __instance, object[] __args) => !GuardedInventoryMethods.Blocked(__instance, __args);
}
