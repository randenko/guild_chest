using System;
using System.Collections;
using System.Linq;
using BepInEx.Bootstrap;
using GuildChest.Core;
using HarmonyLib;
using UnityEngine;

namespace GuildChest.NativeSmoke;

public sealed partial class NativeSmokeHarness
{
    private static float peekDelay, openDelay;
    private static bool dropNextPeek;
    private static Type Compat => typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.StorageCompatibility")!;
    private static Type ClientType => typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.Client")!;
    private static Type AutoFunctions => Chainloader.PluginInfos["Azumatt.AzuAutoStore"].Instance.GetType().Assembly.GetType("AzuAutoStore.Util.Functions")!;
    private static Type AutoBoxes => AutoFunctions.Assembly.GetType("AzuAutoStore.Util.Boxes")!;
    private static Type CraftyBoxes => Chainloader.PluginInfos["Azumatt.AzuCraftyBoxes"].Instance.GetType().Assembly.GetType("AzuCraftyBoxes.Util.Functions.Boxes")!;
    private static bool TransferPending => (bool)ClientType.GetProperty("Pending", Flags)!.GetValue(null);
    private static int Swords(Player player) => player.GetInventory().GetAllItems().Count(item => item.m_dropPrefab.name == "SwordIron");
    private static void SetToggle(Type plugin, string field, int value)
    {
        var setting = AccessTools.Field(plugin, field).GetValue(null);
        setting.GetType().GetProperty("Value")!.SetValue(setting, Enum.ToObject(setting.GetType().GetProperty("Value")!.PropertyType, value));
    }
    private static void Add(Inventory inventory, string prefab, int amount, int quality = 1)
    {
        var obj = ObjectDB.instance.GetItemPrefab(prefab);
        while (amount > 0)
        {
            var item = obj.GetComponent<ItemDrop>().m_itemData.Clone();
            item.m_dropPrefab = obj; item.m_quality = quality;
            item.m_stack = Math.Min(amount, item.m_shared.m_maxStackSize);
            amount -= item.m_stack;
            Check(inventory.AddItem(item), "Regression fixture item fits");
        }
    }
    private static void AutoOrder(params Container[] containers)
    {
        ((IList)AccessTools.Field(AutoBoxes, "Containers").GetValue(null)).Clear();
        foreach (var container in containers) AccessTools.Method(AutoBoxes, "AddContainer").Invoke(null, new object[] { container });
    }
    private static void AutoStore() => AccessTools.Method(AutoFunctions, "TryStore", Type.EmptyTypes).Invoke(null, null);
    private static void CraftFish(Player player, int multiplier = 1)
    {
        var recipe = ScriptableObject.CreateInstance<Recipe>(); recipe.name = "GuildChestQualityRegression";
        recipe.m_enabled = true; recipe.m_amount = 1; recipe.m_requireOnlyOneIngredient = true;
        recipe.m_qualityResultAmountMultiplier = 0f;
        recipe.m_item = ObjectDB.instance.GetItemPrefab("SwordIron").GetComponent<ItemDrop>();
        recipe.m_resources = new[] { new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Fish1").GetComponent<ItemDrop>(), m_amount = 2 } };
        AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").SetValue(InventoryGui.instance, recipe);
        AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem").SetValue(InventoryGui.instance, null);
        AccessTools.Field(typeof(InventoryGui), "m_multiCrafting").SetValue(InventoryGui.instance, multiplier != 1);
        AccessTools.Field(typeof(InventoryGui), "m_multiCraftAmount").SetValue(InventoryGui.instance, multiplier);
        AccessTools.Method(typeof(InventoryGui), "DoCrafting").Invoke(InventoryGui.instance, new object[] { player });
    }
    private IEnumerator SeedFish(Player player, Container chest, int amount, int quality)
    {
        bool? completed = null;
        try
        {
            Add(player.GetInventory(), "Fish1", amount, quality);
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) =>
            {
                foreach (var fish in personal.GetAllItems().Where(item => item.m_dropPrefab.name == "Fish1" && item.m_quality == quality).ToList())
                    shared.MoveItemToThis(personal, fish);
            }, (ok, _) => completed = ok), "Fish stock transfer starts");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try { Check(completed == true, "Fish stock transfer acknowledged"); }
        catch (Exception exception) { Fail(exception); }
    }
    private IEnumerator RunStorageRegressions(Player player, Container chest, Container ordinary, Container second)
    {
        yield return AutoStoreRegressions(player, chest, ordinary, second);
        if (System.IO.File.Exists(Marker + ".failed")) yield break;
        yield return QualityRegressions(player, chest, ordinary);
        if (System.IO.File.Exists(Marker + ".failed")) yield break;
        yield return PreviewRegressions(player, chest, second);
    }
    private IEnumerator AutoStoreRegressions(Player player, Container chest, Container ordinary, Container second)
    {
        var autoPlugin = Chainloader.PluginInfos["Azumatt.AzuAutoStore"].Instance.GetType();
        int ironBefore = Decode(Store!.Snapshot().Inventory).CountItems("$item_iron");
        int effects = transferEffects;
        try
        {
            SetToggle(autoPlugin, "MustHaveExistingItemToPull", 1);
            ordinary.GetInventory().RemoveAll(); Add(ordinary.GetInventory(), "Stone", 1);
            Add(player.GetInventory(), "Iron", 2); Add(player.GetInventory(), "Stone", 3);
            AutoOrder(chest, second, ordinary); AutoStore();
            Check(TransferPending && ordinary.GetInventory().CountItems("$item_stone") == 1, "Ordinary deposit waits for guild ACK");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(player.GetInventory().CountItems("$item_stone") == 0 && ordinary.GetInventory().CountItems("$item_stone") == 4, "One hotkey resumes ordinary deposit after guild ACK");
            Check(Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == ironBefore + 2, "Aliases deposit once");
            Check(transferEffects == effects + 2, "Guild and ordinary destinations each play one effect");
            Add(player.GetInventory(), "Iron", 2); Add(player.GetInventory(), "Stone", 3);
            AutoOrder(ordinary, chest, second); AutoStore();
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(player.GetInventory().CountItems("$item_stone") == 0 && ordinary.GetInventory().CountItems("$item_stone") == 7, "Ordinary-first order also deposits both resources");
            Check(Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == ironBefore + 4, "Ordinary-first guild deposit once");
            // A successful no-op must release the queue too.
            Add(player.GetInventory(), "Stone", 3); AutoOrder(chest, ordinary); AutoStore();
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(ordinary.GetInventory().CountItems("$item_stone") == 10, "Guild no-op resumes ordinary destinations");
            Store!.Open(ZNet.GetUID(), second.GetComponent<ZNetView>().GetZDO().m_uid.ToString(), Time.unscaledTime, out _);
            Add(player.GetInventory(), "Iron", 2); Add(player.GetInventory(), "Stone", 3); AutoStore();
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(player.GetInventory().CountItems("$item_iron") == 2 && ordinary.GetInventory().CountItems("$item_stone") == 13, "Busy guild lease preserves iron and continues ordinary deposits");
            Store!.Close(ZNet.GetUID(), Store.ActiveLease!.Token);
            // Single-item deposits must not change unrelated items.
            var stone = ObjectDB.instance.GetItemPrefab("Stone").GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
            Add(player.GetInventory(), "Stone", 3);
            var selected = player.GetInventory().GetAllItems().Single(item => item.m_shared.m_name == stone);
            AccessTools.Method(AutoFunctions, "TryStoreThisItem").Invoke(null, new object[] { selected, player.GetInventory() });
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(ordinary.GetInventory().CountItems("$item_stone") == 16 && player.GetInventory().CountItems("$item_iron") == 2, "Single-item continuation deposits only its selected item");
            // Simulate a remote open response; the queue must wait during opening.
            openDelay = 0.5f; Add(player.GetInventory(), "Stone", 3); AutoStore();
            Check((bool)ClientType.GetProperty("Opening", Flags)!.GetValue(null), "Delayed open is in flight");
            Check(ordinary.GetInventory().CountItems("$item_stone") == 16, "Ordinary writes wait during delayed open");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(ordinary.GetInventory().CountItems("$item_stone") == 19 && player.GetInventory().CountItems("$item_iron") == 0, "Delayed open/commit resumes all destinations");
            openDelay = 0;
            AutoOrder(chest, second); SetToggle(autoPlugin, "MustHaveExistingItemToPull", 0);
            Logger.LogInfo("NATIVE_SMOKE_AUTOSTORE_CONTINUATION_PASSED");
        }
        catch (Exception exception) { Fail(exception); }
    }
    private IEnumerator QualityRegressions(Player player, Container chest, Container ordinary)
    {
        string fishName = ObjectDB.instance.GetItemPrefab("Fish1").GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
        var craftyPlugin = Chainloader.PluginInfos["Azumatt.AzuCraftyBoxes"].Instance.GetType();
        SetToggle(craftyPlugin, "leaveOne", 0);
        yield return SeedFish(player, chest, 2, 2);
        int swords = Swords(player);
        try { Add(player.GetInventory(), "Fish1", 1); CraftFish(player); Check(TransferPending, "Mixed-quality recipe waits for acknowledged supply"); }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Logger.LogInfo($"Quality regression: swords={Swords(player)} before={swords}; playerQ1={player.GetInventory().CountItems(fishName, 1)} playerQ2={player.GetInventory().CountItems(fishName, 2)} guildQ2={Decode(Store!.Snapshot().Inventory).CountItems(fishName, 2)} pending={TransferPending}");
            Check(Swords(player) == swords + 1 && player.GetInventory().CountItems(fishName, 1) == 1, "Recipe consumes two quality-2 fish and retains quality-1 fish");
            Check(Decode(Store!.Snapshot().Inventory).CountItems(fishName, 2) == 0 && player.GetInventory().CountItems(fishName, 2) == 0, "Matching quality consumed exactly once");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return SeedFish(player, chest, 4, 2);
        try { swords = Swords(player); CraftFish(player, 2); }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(Swords(player) == swords + 2 && player.GetInventory().CountItems(fishName, 1) == 1 && Decode(Store!.Snapshot().Inventory).CountItems(fishName, 2) == 0, "Batch cost uses four matching-quality fish");
            AccessTools.Field(typeof(InventoryGui), "m_multiCrafting").SetValue(InventoryGui.instance, false);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return SeedFish(player, chest, 2, 2);
        yield return SeedFish(player, chest, 1, 3);
        try { SetToggle(craftyPlugin, "leaveOne", 1); swords = Swords(player); CraftFish(player); }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(Swords(player) == swords + 1 && Decode(Store!.Snapshot().Inventory).CountItems(fishName, 3) == 1, "Leave One reserves an ingredient, rather than one of every quality");
            SetToggle(craftyPlugin, "leaveOne", 0);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return SeedFish(player, chest, 1, 2);
        try
        {
            swords = Swords(player); var before = Encode(player.GetInventory()); var sharedBefore = Store!.Snapshot().Inventory;
            CraftFish(player);
            Check(!TransferPending && Swords(player) == swords && before.SequenceEqual(Encode(player.GetInventory())) && sharedBefore.SequenceEqual(Store!.Snapshot().Inventory), "Insufficient matching quality changes neither inventory");
            Add(ordinary.GetInventory(), "Fish1", 2, 4);
            ordinary.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_creator, player.GetPlayerID());
            AccessTools.Method(CraftyBoxes, "AddContainer").Invoke(null, new object[] { ordinary });
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(1);
        try
        {
            var sharedBefore = Store!.Snapshot().Inventory; swords = Swords(player); CraftFish(player);
            Check(!TransferPending && Swords(player) == swords + 1 && ordinary.GetInventory().CountItems(fishName, 4) == 0 && sharedBefore.SequenceEqual(Store!.Snapshot().Inventory), "Existing ordinary ingredient choice crafts without guild withdrawal");
            AccessTools.Method(CraftyBoxes, "RemoveContainer").Invoke(null, new object[] { ordinary });
            Add(player.GetInventory(), "Fish1", 1); swords = Swords(player); CraftFish(player);
            Check(!TransferPending && Swords(player) == swords + 1 && player.GetInventory().CountItems(fishName, 1) == 0, "Existing player ingredient choice retained");
            Logger.LogInfo("NATIVE_SMOKE_INGREDIENT_QUALITY_PASSED");
        }
        catch (Exception exception) { Fail(exception); }
    }
    private IEnumerator PreviewRegressions(Player player, Container chest, Container second)
    {
        void ForcePoll() { AccessTools.Field(Compat, "nextPeek").SetValue(null, 0f); AccessTools.Method(Compat, "Tick").Invoke(null, null); }
        long Pending() => (long)AccessTools.Field(Compat, "pendingPeek").GetValue(null);
        Inventory? Snapshot() => AccessTools.Field(Compat, "snapshot").GetValue(null) as Inventory;
        long Revision() => (long)AccessTools.Field(Compat, "snapshotRevision").GetValue(null);
        try { AccessTools.Method(Compat, "Reset").Invoke(null, null); peekDelay = 1.2f; ForcePoll(); Check(Pending() != 0, "Slow Peek in flight"); }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(1.4f);
        try { Check(Snapshot() != null, "First 1.2-second Peek reply accepted"); }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(4);
        bool? completed = null;
        try
        {
            Check(Snapshot() != null && Snapshot()!.CountItems("$item_iron") == Decode(Store!.Snapshot().Inventory).CountItems("$item_iron"), "Sustained slow polling keeps material counts available");
            var piece = new GameObject("GuildChestSlowPreviewFixture").AddComponent<Piece>();
            piece.m_resources = new[] { new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>(), m_amount = 1 } };
            Check(player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "Slow preview still funds native build requirement check");
            // Force a preview of the pre-commit revision, then commit before it arrives.
            AccessTools.Field(Compat, "pendingPeek").SetValue(null, 0L); ForcePoll();
            Add(player.GetInventory(), "Iron", 1);
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) => shared.StackAll(personal), (ok, _) => completed = ok), "Commit while an old preview travels");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(1.4f);
        try
        {
            Check(completed == true && Revision() == Store!.Snapshot().Revision && Snapshot()!.CountItems("$item_iron") == Decode(Store.Snapshot().Inventory).CountItems("$item_iron"), "Old Peek cannot overwrite ACK-confirmed stock");
            peekDelay = 0; AccessTools.Field(Compat, "pendingPeek").SetValue(null, 0L); dropNextPeek = true; ForcePoll();
            long expired = Pending(); var id = (ZDOID)AccessTools.Field(Compat, "peekChest").GetValue(null);
            Check(expired != 0, "Dropped Peek remains pending");
            AccessTools.Field(Compat, "peekSentTime").SetValue(null, Time.unscaledTime - 11); ForcePoll();
            Check((long)AccessTools.Field(Compat, "peekSequence").GetValue(null) > expired && Snapshot() != null, "Timed-out Peek retries and refreshes stock");
            var snapshot = Snapshot();
            AccessTools.Method(Compat, "ReceiveSnapshot").Invoke(null, new object[] { id, expired, AccessResult.Accepted, 0L, Array.Empty<byte>() });
            Check(ReferenceEquals(snapshot, Snapshot()), "Expired reply ignored before decoding");
            AccessTools.Method(Compat, "Reset").Invoke(null, null);
            AccessTools.Method(Compat, "ReceiveSnapshot").Invoke(null, new object[] { id, expired, AccessResult.Accepted, 0L, Array.Empty<byte>() });
            Check(Snapshot() == null && Pending() == 0, "World preview reset rejects late replies");
            peekDelay = 1.2f; ForcePoll();
        }
        catch (Exception exception) { Fail(exception); yield break; }
        var original = (Container)AccessTools.Field(Compat, "snapshotChest").GetValue(null);
        var originalPosition = original.transform.position;
        try
        {
            original.transform.position = player.transform.position + Vector3.right * 30; ForcePoll();
            Check(!ReferenceEquals(AccessTools.Field(Compat, "snapshotChest").GetValue(null), original) && Snapshot() == null, "Changing access point invalidates pending preview");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(1.4f);
        try
        {
            Check(Snapshot() != null && !ReferenceEquals(AccessTools.Field(Compat, "snapshotChest").GetValue(null), original), "Late old-chest reply cannot replace new-chest preview");
            original.transform.position = originalPosition; peekDelay = 0; ForcePoll();
            Logger.LogInfo("NATIVE_SMOKE_PREVIEW_POLLING_PASSED");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
    }
}
