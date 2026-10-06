using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace GuildChest;

[HarmonyPatch(typeof(Container), nameof(Container.Interact))]
internal static class OpenPatch
{
    private static bool Prefix(Container __instance, bool hold, ref bool __result)
    {
        if (Client.Pending) { __result = false; return false; }
        if (!Plugin.IsGuild(__instance)) return true;
        if (!hold) Client.Open(__instance);
        __result = !hold; return false;
    }
}

[HarmonyPatch]
internal static class VanillaContainerWritesPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "Save", "OnDestroyed", "RPC_RequestOpen", "RPC_OpenResponse", "RPC_RequestStack", "RPC_StackResponse", "RPC_RequestTakeAll", "RPC_TakeAllResponse" })
            yield return AccessTools.Method(typeof(Container), name);
    }
    private static bool Prefix(Container __instance) => !Plugin.IsGuild(__instance);
}

[HarmonyPatch(typeof(Container), "Load")]
internal static class ContainerLoadPatch
{
    private static bool Prefix(Container __instance, ref bool __result)
    {
        if (!Plugin.IsGuild(__instance)) return true;
        __result = false; return false;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.IsOwner))]
internal static class ContainerOwnerPatch
{
    private static bool Prefix(Container __instance, ref bool __result)
    {
        if (!Plugin.IsGuild(__instance)) return true;
        __result = Client.Owns(__instance); return false;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.SetInUse))]
internal static class ContainerUsePatch
{
    private static bool Prefix(Container __instance, bool inUse)
    {
        if (!Plugin.IsGuild(__instance)) return true;
        if (__instance.m_open) __instance.m_open.SetActive(inUse);
        if (__instance.m_closed) __instance.m_closed.SetActive(!inUse);
        return false;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
internal static class ContainerHoverPatch
{
    private static bool Prefix(Container __instance, ref string __result)
    {
        if (!Plugin.IsGuild(__instance)) return true;
        __result = Localization.instance.Localize("Guild Chest\n[<color=yellow><b>$KEY_Use</b></color>] $piece_container_open"); return false;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.StackAll))]
internal static class ContainerStackPatch
{
    private static bool Prefix(Container __instance)
    {
        if (!Plugin.IsGuild(__instance)) return true;
        if (Client.Owns(__instance)) Client.Bulk(false); else Client.Open(__instance, QuickAction.Stack);
        return false;
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.TakeAll))]
internal static class ContainerTakePatch
{
    private static bool Prefix(Container __instance, ref bool __result)
    {
        if (Client.Pending) { __result = false; return false; }
        if (!Plugin.IsGuild(__instance)) return true;
        if (Client.Owns(__instance)) Client.Bulk(true); else Client.Open(__instance, QuickAction.Take);
        __result = true; return false;
    }
}

[HarmonyPatch(typeof(InventoryGui), "OnSelectedItem")]
internal static class SelectPatch
{
    private static bool Prefix(InventoryGui __instance, InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos, InventoryGrid.Modifier mod) => Client.Select(__instance, grid, item, pos, mod);
}

[HarmonyPatch]
internal static class BulkPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InventoryGui), "OnTakeAll");
        yield return AccessTools.Method(typeof(InventoryGui), "OnStackAll");
    }
    private static bool Prefix(MethodBase __originalMethod)
    {
        if (Client.Pending) return false;
        if (!Client.GuildUi) return true;
        Client.Bulk(__originalMethod.Name == "OnTakeAll"); return false;
    }
}

[HarmonyPatch]
internal static class ClosePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(InventoryGui), nameof(InventoryGui.Hide));
        yield return AccessTools.Method(typeof(InventoryGui), "CloseContainer");
    }
    private static void Prefix() { if (Client.GuildUi) Client.Close(); }
}

[HarmonyPatch(typeof(InventoryGui), "OnRightClickItem")]
internal static class RightClickPatch
{
    private static bool Prefix(InventoryGrid grid)
    {
        if (!Client.GuildUi) return true;
        if (Client.Pending) return false;
        if (grid.GetInventory() != Client.View) return true;
        Plugin.Message("Move the item into your inventory before using it."); return false;
    }
}

[HarmonyPatch(typeof(InventoryGui), "OnDropOutside")]
internal static class DropPatch
{
    private static bool Prefix(InventoryGui __instance)
    {
        if (Client.Pending) return false;
        if (!Client.GuildUi || AccessTools.FieldRefAccess<InventoryGui, Inventory>("m_dragInventory")(__instance) != Client.View) return true;
        Client.ClearDrag(); Plugin.Message("Move the item into your inventory before dropping it."); return false;
    }
}

// Keep the real player inventory stable until the staged transfer is acknowledged.
[HarmonyPatch]
internal static class PendingPlayerActionsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Player), "AutoPickup");
        yield return AccessTools.Method(typeof(Humanoid), "UseItem");
        yield return AccessTools.Method(typeof(InventoryGui), "DoCrafting");
    }
    private static bool Prefix(object __instance) => !Client.Pending || (__instance is Humanoid humanoid && humanoid != Player.m_localPlayer);
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.Pickup))]
internal static class PendingPickupPatch
{
    private static bool Prefix(Humanoid __instance, ref bool __result)
    {
        if (__instance != Player.m_localPlayer || !Client.Pending) return true;
        __result = false; return false;
    }
}

[HarmonyPatch(typeof(Humanoid), nameof(Humanoid.DropItem))]
internal static class PendingDirectDropPatch
{
    private static bool Prefix(Humanoid __instance, ref bool __result)
    {
        if (__instance != Player.m_localPlayer || !Client.Pending) return true;
        __result = false; return false;
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
internal static class DeathPatch
{
    private static bool Prefix(Player __instance) => !Client.DeferDeath(__instance);
    private static void Postfix(Player __instance) { if (__instance == Player.m_localPlayer && !Client.Pending) Client.Close(); }
}

[HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize))]
internal static class ReceiveWorldObjectPatch
{
    private static bool Prefix(ZDO __instance) => !(Host.IsServer && Host.IsState(__instance));
    private static void Postfix(ZDO __instance) => Host.Register(__instance);
}

[HarmonyPatch(typeof(ZDO), nameof(ZDO.SetPrefab))]
internal static class NewWorldObjectPatch
{
    private static void Postfix(ZDO __instance) => Host.Register(__instance);
}

[HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
internal static class DestroyWorldObjectPatch
{
    private static void Prefix(ZDOMan __instance, ZDOID uid)
    {
        var zdo = __instance.GetZDO(uid);
        if (zdo == null) return;
        try { Host.Destroyed(zdo); } catch (Exception exception) { Plugin.Error(exception); }
    }
}

[HarmonyPatch]
internal static class StateOwnerPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwner));
        yield return AccessTools.Method(typeof(ZDO), nameof(ZDO.SetOwnerInternal));
    }
    private static bool Prefix(ZDO __instance, long uid) => !(Host.IsServer && Host.IsState(__instance) && uid != ZNet.GetUID());
}

[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.PrepareSave))]
internal static class WorldSavePatch
{
    private static void Prefix() => Host.Persist();
}

// ZNetScene.Awake precedes loading the world's ZDOs; initialize only after both old and chunked saves load.
[HarmonyPatch(typeof(ZNet), "ServerLoadWorld")]
internal static class WorldLoadedPatch
{
    private static void Postfix()
    {
        try { Host.Initialize(); } catch (Exception exception) { Plugin.Error(exception); }
    }
}

[HarmonyPatch(typeof(ZNet), "Shutdown")]
internal static class ShutdownPatch
{
    private static void Prefix() { Host.Persist(); Client.Reset(); Host.Reset(); }
}
