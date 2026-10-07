using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace GuildChest;

/// <summary>Client-side storage integration. Call on Unity's main thread.</summary>
public static class GuildChestStorage
{
    public static bool IsGuildChest(Container container) => Plugin.IsGuild(container);

    /// <summary>
    /// Acquires the server lease and runs a transfer against disposable player/shared
    /// inventories. Move items between these inventories; do not retain their references
    /// or perform gameplay side effects. Completion follows the server acknowledgement.
    /// False means the request could not start; neither inventory has changed.
    /// </summary>
    public static bool TryTransfer(Container chest, Action<Inventory, Inventory> transfer, Action<bool, string>? completed = null)
    {
        if (!Plugin.IsGuild(chest) || transfer == null) return false;
        return Client.Transfer(chest, transfer, completed);
    }
}

internal static class StorageCompatibility
{
    private static readonly ConditionalWeakTable<Inventory, object> protectedInventories = new();
    private static readonly object marker = new();
    internal static int UiDepth, PreviewDepth, ConsumptionDepth;
    internal static Container? ScopedChest;
    internal static Inventory? ScopedShared;
    private static Container? snapshotChest;
    private static Inventory? snapshot;
    private static long peekSequence;
    private static long pendingPeek, snapshotRevision = -1;
    private static ZDOID peekChest;
    private static float nextPeek, peekSentTime, snapshotTime;
    private static Type? autoFunctions, craftyBoxes, craftyPlugin, craftyMisc, craftyUiBank;
    private static MethodInfo? autoStoreAll, autoStoreSingle;
    private static bool installing;

    internal static void Protect(Inventory inventory) => protectedInventories.GetValue(inventory, _ => marker);
    internal static bool Blocked(Inventory inventory) => protectedInventories.TryGetValue(inventory, out _) ||
        (Client.Pending && !Client.Applying && Player.m_localPlayer && inventory == Player.m_localPlayer.GetInventory());
    internal static void Track(Container container)
    {
        if (!Plugin.IsGuild(container)) return;
        // Never leave a writable, disconnected vanilla inventory available to automation.
        var unavailable = new Inventory("Guild Chest (transaction required)", null, 0, 0);
        Protect(unavailable);
        AccessTools.FieldRefAccess<Container, Inventory>("m_inventory")(container) = unavailable;
    }
    internal static Inventory InventoryFor(Container container)
    {
        if (container == ScopedChest) return ScopedShared!;
        if (UiDepth > 0 && Client.Owns(container)) return Client.View!;
        var stored = AccessTools.FieldRefAccess<Container, Inventory>("m_inventory")(container);
        if (stored == Client.View || stored.GetWidth() != 0)
        {
            var unavailable = new Inventory("Guild Chest (transaction required)", null, 0, 0);
            Protect(unavailable); return unavailable;
        }
        return stored;
    }
    internal static void Reset()
    {
        snapshot = null; snapshotChest = null; ScopedChest = null; ScopedShared = null;
        nextPeek = snapshotTime = 0; pendingPeek = 0; snapshotRevision = -1;
        AutoStoreBatch.Reset();
    }

    internal static void Install(Harmony harmony)
    {
        if (installing) return;
        installing = true;
        if (Chainloader.PluginInfos.TryGetValue("Azumatt.AzuAutoStore", out var auto))
        {
            var assembly = auto.Instance.GetType().Assembly;
            autoFunctions = assembly.GetType("AzuAutoStore.Util.Functions");
            var wrapper = assembly.GetType("AzuAutoStore.Interfaces.VanillaContainers");
            autoStoreAll = AccessTools.Method(wrapper, "TryStore", Type.EmptyTypes);
            autoStoreSingle = AccessTools.Method(wrapper, "TryStoreThisItem");
            Patch(harmony, AccessTools.Method(autoFunctions, "StoreToContainer"), nameof(AutoStoreContainer));
            Patch(harmony, AccessTools.Method(autoFunctions, "TryStore", new[] { typeof(Container), typeof(ItemDrop.ItemData).MakeByRefType(), typeof(bool), typeof(bool) }), nameof(AutoStoreDirect));
            AutoStoreBatch.Install(harmony, auto.Instance.GetType(), autoFunctions!);
            Plugin.LogInfo($"AzuAutoStore adapter installed ({auto.Metadata.Version}).");
        }
        if (Chainloader.PluginInfos.TryGetValue("Azumatt.AzuCraftyBoxes", out var crafty))
        {
            var assembly = crafty.Instance.GetType().Assembly;
            craftyBoxes = assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes");
            craftyMisc = assembly.GetType("AzuCraftyBoxes.Util.Functions.MiscFunctions");
            craftyPlugin = crafty.Instance.GetType();
            craftyUiBank = assembly.GetType("AzuCraftyBoxes.Util.Functions.UiItemBank");
            var wrapper = assembly.GetType("AzuCraftyBoxes.IContainers.VanillaContainer");
            Patch(harmony, AccessTools.Method(wrapper, "ItemCount"), nameof(CraftyCount));
            Patch(harmony, AccessTools.Method(wrapper, "GetInventory"), nameof(CraftyInventory));
            Patch(harmony, AccessTools.Method(wrapper, "ProcessContainerInventory"), nameof(CraftyConsume));
            Patch(harmony, AccessTools.Method(wrapper, "RemoveItem"), nameof(CraftyRemove));
            Patch(harmony, AccessTools.Method(craftyBoxes, "MemoUsable"), nameof(CraftyMemo));
            Plugin.LogInfo($"AzuCraftyBoxes adapter installed ({crafty.Metadata.Version}).");
        }
    }
    private static void Patch(Harmony harmony, MethodInfo? method, string prefix)
    {
        if (method == null) { Plugin.LogWarning($"Storage adapter hook {prefix} unavailable; unadapted guild writes remain blocked."); return; }
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(StorageCompatibility), prefix) { priority = Priority.First });
    }
    internal static Container? WrappedContainer(object wrapper) => wrapper.GetType()
        .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(field => field.FieldType == typeof(Container)).Select(field => field.GetValue(wrapper) as Container).FirstOrDefault();

    private static bool AutoStoreDirect(Container nearbyContainer, ref bool __result)
    {
        if (!Plugin.IsGuild(nearbyContainer) || nearbyContainer == ScopedChest) return true;
        // Ground-item automation has no player transaction to acknowledge. Decline it
        // rather than let it remove a drop after writing to an orphan inventory.
        __result = false; return false;
    }
    private static bool AutoStoreContainer(object container, ItemDrop.ItemData? singleItem, Inventory? inv, ref int __result)
    {
        var chest = WrappedContainer(container);
        if (!Plugin.IsGuild(chest)) return !Client.Pending;
        __result = 0;
        StoreGuild(container, singleItem, inv, null);
        return false;
    }
    internal static bool StoreGuild(object container, ItemDrop.ItemData? singleItem, Inventory? inv, Action? completed)
    {
        var chest = WrappedContainer(container);
        if (autoStoreAll == null || autoStoreSingle == null || Client.Pending || Client.Opening) return false;
        int stored = 0;
        return GuildChestStorage.TryTransfer(chest!, (player, shared) =>
        {
            // Re-run the mod's own rules (favorites, hotbar, equipment, YAML, existing
            // stacks) against clones after obtaining the authoritative snapshot.
            ItemDrop.ItemData? staged = null;
            if (singleItem != null)
            {
                var real = Player.m_localPlayer.GetInventory();
                if (inv != real || !real.ContainsItem(singleItem)) return;
                staged = player.GetItemAt(singleItem.m_gridPos.x, singleItem.m_gridPos.y);
            }
            ref var live = ref AccessTools.FieldRefAccess<Humanoid, Inventory>("m_inventory")(Player.m_localPlayer);
            ref var local = ref AccessTools.FieldRefAccess<Container, Inventory>("m_inventory")(chest!);
            var originalPlayer = live; var originalChest = local;
            try
            {
                live = player; local = shared; ScopedChest = chest; ScopedShared = shared;
                stored = (int)(singleItem == null ? autoStoreAll.Invoke(container, null) : autoStoreSingle.Invoke(container, new object?[] { staged, player }));
            }
            finally
            {
                live = originalPlayer; local = originalChest; ScopedChest = null; ScopedShared = null;
                // The adapter plays this destination's effect after ACK. Do not let
                // a later ordinary StoreSuccess play it a second time.
                AutoStoreBatch.ForgetPing(container);
            }
        }, (accepted, reason) =>
        {
            try
            {
                if (accepted && stored > 0)
                {
                    Plugin.Message($"Stored {stored} items in guild storage.");
                    if (chest)
                    {
                        // Use AutoStore's configured ping/highlight and the game's transfer
                        // effect, but only once the deposit has been acknowledged.
                        AccessTools.Method(autoFunctions, "PingContainer")?.Invoke(null, new object[] { chest!.gameObject });
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

    private static List<object> CraftyNearby()
    {
        if (craftyBoxes == null || !Player.m_localPlayer || (bool)AccessTools.Method(craftyMisc, "ShouldPrevent").Invoke(null, null)) return new();
        var range = (float)AccessTools.Field(craftyPlugin, "mRange").GetValue(null).GetType().GetProperty("Value")!.GetValue(AccessTools.Field(craftyPlugin, "mRange").GetValue(null));
        var query = AccessTools.Method(craftyBoxes, "GetNearbyContainers").MakeGenericMethod(typeof(Player));
        return ((IEnumerable)query.Invoke(null, new object[] { Player.m_localPlayer, range })).Cast<object>().ToList();
    }
    private static bool LeaveOne()
    {
        var setting = AccessTools.Field(craftyPlugin, "leaveOne").GetValue(null);
        return Convert.ToInt32(setting.GetType().GetProperty("Value")!.GetValue(setting)) != 0;
    }
    private static bool Pullable(object wrapper, string prefab) =>
        (bool)AccessTools.Method(craftyBoxes, "CanItemBePulled").Invoke(null, new[] { AccessTools.Method(wrapper.GetType(), "GetPrefabName").Invoke(wrapper, null), prefab, "" });

    internal static void Tick()
    {
        AutoStoreBatch.Tick();
        if (craftyBoxes == null || !ZNet.instance || !Player.m_localPlayer || Player.m_localPlayer.IsDead() || Time.unscaledTime < nextPeek) return;
        nextPeek = Time.unscaledTime + 1f;
        try
        {
            var selected = CraftyNearby().Select(WrappedContainer).Where(chest => Plugin.IsGuild(chest) &&
                Vector3.Distance(chest!.transform.position, Player.m_localPlayer.transform.position) <= Plugin.AutomationRange.Value)
                .OrderBy(chest => Vector3.Distance(chest!.transform.position, Player.m_localPlayer.transform.position)).FirstOrDefault();
            if (!selected) { snapshot = null; snapshotChest = null; pendingPeek = 0; return; }
            if (snapshotChest != selected) { snapshot = null; pendingPeek = 0; }
            snapshotChest = selected;
            // A new poll must not supersede a reply still travelling from the host.
            // Retry a lost request after ten seconds; late replies cannot match it.
            if (pendingPeek != 0 && Time.unscaledTime - peekSentTime < 10f) return;
            pendingPeek = ++peekSequence; peekSentTime = Time.unscaledTime;
            peekChest = selected!.GetComponent<ZNetView>().GetZDO().m_uid;
            var package = new ZPackage(); package.Write((int)Operation.Peek); package.Write(selected!.GetComponent<ZNetView>().GetZDO().m_uid);
            package.Write(""); package.Write(pendingPeek); Plugin.RequestRpc.SendPackage(Plugin.ServerId, package);
        }
        catch (Exception exception) { snapshot = null; pendingPeek = 0; Plugin.Error(exception); }
    }
    internal static void ReceiveSnapshot(ZDOID id, long sequence, GuildChest.Core.AccessResult result, long revision, byte[] bytes)
    {
        if (!snapshotChest || pendingPeek == 0 || sequence != pendingPeek || id != peekChest || snapshotChest!.GetComponent<ZNetView>().GetZDO()?.m_uid != id) return;
        pendingPeek = 0;
        nextPeek = Math.Min(nextPeek, Math.Max(Time.unscaledTime, peekSentTime + 1f));
        if (result == GuildChest.Core.AccessResult.Accepted) RefreshSnapshot(bytes, revision);
        else { snapshot = null; InvalidateCounts(); }
    }
    internal static void RefreshSnapshot(byte[] bytes, long revision)
    {
        // A Peek sent before a commit can arrive after that commit's ACK.
        if (revision < snapshotRevision) return;
        snapshotRevision = revision;
        if (!snapshotChest) return;
        snapshot = InventoryCodec.Read(bytes); Protect(snapshot); snapshotTime = Time.unscaledTime;
        InvalidateCounts();
    }
    private static Inventory? Preview(Container? chest) => PreviewDepth > 0 && ConsumptionDepth == 0 && !Client.Pending && chest &&
        chest == snapshotChest && Time.unscaledTime - snapshotTime < 3 && Player.m_localPlayer &&
        Vector3.Distance(chest.transform.position, Player.m_localPlayer.transform.position) <= Plugin.AutomationRange.Value ? snapshot : null;
    private static bool CraftyCount(object __instance, string name, ref int __result)
    {
        var chest = WrappedContainer(__instance);
        if (!Plugin.IsGuild(chest)) return true;
        __result = Preview(chest)?.CountItems(name) ?? 0; return false;
    }
    private static bool CraftyInventory(object __instance, ref Inventory __result)
    {
        var chest = WrappedContainer(__instance);
        if (!Plugin.IsGuild(chest)) return true;
        var preview = Preview(chest);
        if (preview == null) __result = InventoryFor(chest!);
        else { __result = InventoryCodec.Clone(preview); Protect(__result); }
        return false;
    }
    private static bool CraftyConsume(object __instance, int totalAmount, ref int __result)
    {
        if (!Plugin.IsGuild(WrappedContainer(__instance))) return true;
        __result = totalAmount; return false;
    }
    private static bool CraftyRemove(object __instance) => !Plugin.IsGuild(WrappedContainer(__instance));
    private static bool CraftyMemo(ref bool __result)
    {
        // A tally cached while previewing must never fund an unadapted consumer.
        if (PreviewDepth > 0 && ConsumptionDepth == 0) return true;
        __result = false; return false;
    }
    internal static void InvalidateCounts()
    {
        if (craftyBoxes != null) AccessTools.Method(craftyBoxes, "InvalidateCounts")?.Invoke(null, null);
        // UiItemBank caches totals for an entire frame. A zero calculated outside
        // a preview (or before an ACK) must not hide stock in the build HUD later
        // that same frame; a preview total must not fund an unadapted consumer.
        if (craftyUiBank == null) return;
        (AccessTools.Field(craftyUiBank, "_totals")?.GetValue(null) as IDictionary)?.Clear();
        (AccessTools.Field(craftyUiBank, "_containersHavingAny")?.GetValue(null) as IDictionary)?.Clear();
    }

    private sealed class MaterialNeed
    {
        internal readonly string Name;
        internal readonly int Amount, Quality;
        internal MaterialNeed(string name, int amount, int quality = -1) { Name = name; Amount = amount; Quality = quality; }
    }
    private static List<MaterialNeed> OneIngredientNeeds(Recipe recipe, int quality, int multiplier, Inventory player, Inventory shared, List<object> nearby)
    {
        // Let vanilla and CraftyBoxes choose any ingredient they can already use
        // without guild storage, including their ordinary-container preference.
        if (Player.m_localPlayer.GetFirstRequiredItem(player, recipe, quality, out _, out _, multiplier) != null) return new();
        var station = Player.m_localPlayer.GetCurrentCraftingStation();
        var guild = nearby.FirstOrDefault(wrapper => WrappedContainer(wrapper) == snapshotChest);
        foreach (var requirement in recipe.m_resources)
        {
            if (!requirement.m_resItem || requirement.m_upgraderResource != (station && station.m_upgrader)) continue;
            int amount = requirement.GetAmount(quality) * multiplier;
            if (amount <= 0 || guild == null || !Pullable(guild, requirement.m_resItem.gameObject.name)) continue;
            string name = requirement.m_resItem.m_itemData.m_shared.m_name;
            // Leave One reserves one item of this ingredient in the container,
            // just as upstream does; it does not reserve one of every quality.
            int takeable = Math.Max(0, shared.CountItems(name) - (LeaveOne() ? 1 : 0));
            for (int itemQuality = 0; itemQuality <= requirement.m_resItem.m_itemData.m_shared.m_maxQuality; itemQuality++)
            {
                int needed = Math.Max(0, amount - player.CountItems(name, itemQuality));
                if (needed > 0 && needed <= Math.Min(takeable, shared.CountItems(name, itemQuality)))
                    return new() { new MaterialNeed(name, needed, itemQuality) };
            }
        }
        throw new InvalidOperationException("Not enough allowed ingredients of one quality remain in guild storage.");
    }
    private static List<MaterialNeed> Needs(Piece.Requirement[] requirements, int quality, int multiplier, bool onlyOne, Inventory player, Inventory shared, List<object> nearby, bool building, Recipe? recipe)
    {
        if (onlyOne)
        {
            if (!recipe) throw new InvalidOperationException("A recipe is required to select an ingredient quality.");
            return OneIngredientNeeds(recipe!, quality, multiplier, player, shared, nearby);
        }
        var result = new List<MaterialNeed>();
        var station = Player.m_localPlayer.GetCurrentCraftingStation();
        bool leaveOne = LeaveOne();
        foreach (var requirement in requirements)
        {
            if (!requirement.m_resItem || !building && requirement.m_upgraderResource != (station && station.m_upgrader)) continue;
            int amount = requirement.GetAmount(quality) * multiplier;
            if (amount <= 0) continue;
            string name = requirement.m_resItem.m_itemData.m_shared.m_name;
            string prefab = requirement.m_resItem.gameObject.name;
            int available = player.CountItems(name);
            foreach (var container in nearby)
                if (!Plugin.IsGuild(WrappedContainer(container)) && Pullable(container, prefab))
                    available += Math.Max(0, (int)AccessTools.Method(container.GetType(), "ItemCount").Invoke(container, new object[] { name }) - (leaveOne ? 1 : 0));
            int needed = Math.Max(0, amount - available);
            var guild = nearby.FirstOrDefault(wrapper => WrappedContainer(wrapper) == snapshotChest);
            int inGuild = guild != null && Pullable(guild, prefab) ? Math.Max(0, shared.CountItems(name) - (leaveOne ? 1 : 0)) : 0;
            if (needed > inGuild) throw new InvalidOperationException("Not enough allowed resources remain in nearby storage.");
            if (needed > 0) result.Add(new MaterialNeed(name, needed));
        }
        return result;
    }
    internal static bool Supply(Piece.Requirement[] requirements, int quality, int multiplier, bool onlyOne, bool building, Action? ready)
        => SupplyCore(requirements, quality, multiplier, onlyOne, building, ready, null);
    internal static bool SupplyCrafting(Recipe recipe, int quality, int multiplier, Action ready)
        => SupplyCore(recipe.m_resources, quality, multiplier, recipe.m_requireOnlyOneIngredient, false, ready, recipe);
    private static bool SupplyCore(Piece.Requirement[] requirements, int quality, int multiplier, bool onlyOne, bool building, Action? ready, Recipe? recipe)
    {
        if (craftyBoxes == null || !snapshotChest || snapshot == null || Time.unscaledTime - snapshotTime > 3) return false;
        if (Client.Pending || Client.Opening) return true;
        try
        {
            var nearby = CraftyNearby();
            var planned = Needs(requirements, quality, multiplier, onlyOne, Player.m_localPlayer.GetInventory(), snapshot, nearby, building, recipe);
            if (planned.Count == 0) return false;
            bool started = GuildChestStorage.TryTransfer(snapshotChest!, (player, shared) =>
            {
                // Re-check the counts, permissions and rules after acquiring the lease.
                var needs = Needs(requirements, quality, multiplier, onlyOne, player, shared, CraftyNearby(), building, recipe);
                foreach (var need in needs)
                {
                    int remaining = need.Amount;
                    foreach (var source in shared.GetAllItems().Where(item => item.m_shared.m_name == need.Name &&
                        (need.Quality < 0 || item.m_quality == need.Quality) && item.m_worldLevel >= Game.m_worldLevel).ToList())
                    {
                        int amount = Math.Min(remaining, source.m_stack);
                        var copy = source.Clone(); copy.m_stack = amount; copy.m_equipped = false;
                        if (!player.AddItem(copy)) throw new InvalidOperationException("Make room in your inventory for guild crafting materials.");
                        shared.RemoveItem(source, amount); remaining -= amount;
                        if (remaining == 0) break;
                    }
                    if (remaining != 0) throw new InvalidOperationException("Guild crafting resources changed; try again.");
                }
            }, (accepted, reason) =>
            {
                if (!accepted) Plugin.Message($"Guild material transfer failed: {reason}");
                else ready?.Invoke();
            });
            if (!started) Plugin.Message("The guild inventory is busy. Try again.");
            return true;
        }
        catch (InvalidOperationException exception)
        {
            // GetAmount dereferences the chosen one-ingredient item. Aggregate
            // preview counts can enable the button without a usable quality.
            if (onlyOne && recipe) { Plugin.Message(exception.Message); return true; }
            return false;
        }
    }
}
