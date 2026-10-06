using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using GuildChest.Core;
using HarmonyLib;
using UnityEngine;

namespace GuildChest.NativeSmoke;

[BepInPlugin("com.randenko.guildchest.smoke", "Guild Chest Native Verification", "1.0.0")]
[BepInDependency(GuildChest.Plugin.Id)]
public sealed class NativeSmokeHarness : BaseUnityPlugin
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly Type host = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.Host")!;
    private readonly Type codec = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.InventoryCodec")!;
    private bool ran;
    private string Marker => Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE_MARKER")!;
    private SharedStore? Store => (SharedStore?)host.GetField("Store", Flags)!.GetValue(null);
    private IEnumerable<ZDO> Objects => ((Dictionary<ZDOID, ZDO>)typeof(ZDOMan).GetField("m_objectsByID", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ZDOMan.instance)).Values;
    private Inventory Decode(byte[] bytes) => (Inventory)codec.GetMethod("Read", Flags)!.Invoke(null, new object[] { bytes, 8, 4 });
    private byte[] Encode(Inventory inventory) => (byte[])codec.GetMethod("Save", Flags)!.Invoke(null, new object[] { inventory });
    private bool Conserves(Inventory oldShared, Inventory oldPlayer, Inventory newShared, Inventory newPlayer) =>
        (bool)codec.GetMethod("Conserves", Flags)!.Invoke(null, new object[] { oldShared, Encode(oldPlayer), newShared, Encode(newPlayer), 8, 4 });
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private void Awake()
    {
        if (Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE_TRANSPORT") == "1")
            new Harmony("com.randenko.guildchest.smoke.keepalive").Patch(AccessTools.Method(typeof(ZNetScene), "RemoveObjects"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(KeepFixtureObjects)));
    }
    private static bool KeepFixtureObjects() => false;

    private void Update()
    {
        if (ran || Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE") != "1" || Store == null || !ObjectDB.instance || !ZNet.instance) return;
        if (!ZNet.instance.GetWorldName().StartsWith("GuildChestNative", StringComparison.Ordinal)) { ran = true; Logger.LogError("Native smoke tests require a disposable world named GuildChestNative*. No tests were run."); return; }
        ran = true; StartCoroutine(Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE_TRANSPORT") == "1" ? RunTransport() : Run());
    }
    private IEnumerator Run()
    {
        yield return new WaitForSeconds(2);
        if (!File.Exists(Marker))
        {
            try
            {
                var prefab = ZNetScene.instance.GetPrefab("GuildChest");
                Check(prefab != null, "Chest prefab registered");
                Check(prefab!.GetComponent<Container>().m_width == 8 && prefab!.GetComponent<Container>().m_height == 4, "Chest dimensions");
                var piece = prefab.GetComponent<Piece>();
                Check(piece.m_craftingStation.name.StartsWith("forge"), "Forge recipe");
                Check(piece.m_resources.Length == 3 && piece.m_resources.Sum(req => req.m_amount) == 32, "Recipe costs");
                UnityEngine.Object.Instantiate(prefab, new Vector3(5000, 1000, 5000), Quaternion.identity);
                UnityEngine.Object.Instantiate(prefab, new Vector3(-5000, 1000, -5000), Quaternion.identity);
                Check(Store!.ChestCount == 2, "World creation hooks register both chests");
                var inventory = new Inventory("fixture", null, 8, 4);
                var ore = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>().m_itemData.Clone();
                ore.m_dropPrefab = ObjectDB.instance.GetItemPrefab("Iron"); ore.m_stack = 12; ore.m_gridPos = new Vector2i(0, 0);
                var weapon = ObjectDB.instance.GetItemPrefab("SwordIron").GetComponent<ItemDrop>().m_itemData.Clone();
                weapon.m_dropPrefab = ObjectDB.instance.GetItemPrefab("SwordIron"); weapon.m_quality = 3; weapon.m_durability = 12.34f;
                weapon.m_gridPos = new Vector2i(1, 0); weapon.m_crafterID = 12345; weapon.m_crafterName = "Fixture"; weapon.m_customData["fixture"] = "preserved";
                inventory.GetAllItems().Add(ore); inventory.GetAllItems().Add(weapon);
                byte[] bytes = Encode(inventory); ValidateItems(Decode(bytes));
                var chest = Objects.First(zdo => zdo.GetPrefab() == "GuildChest".GetStableHashCode());
                var result = Store.Open(ZNet.GetUID(), chest.m_uid.ToString(), Time.unscaledTime, out var lease);
                Check(result == AccessResult.Accepted, "Fixture lease");
                Check(Store.Commit(ZNet.GetUID(), lease!.Token, Store.Snapshot().Revision, 1, bytes, Time.unscaledTime) == AccessResult.Accepted, "Fixture commit");
                Store.Close(ZNet.GetUID(), lease.Token);
                host.GetMethod("Persist", Flags)!.Invoke(null, null);
                ZNet.instance.Save(true);
                File.WriteAllText(Marker, "phase1: recipe, registration, serialization and world save passed\n");
                Logger.LogInfo("NATIVE_SMOKE_PHASE1_PASSED");
            }
            catch (Exception exception) { Fail(exception); yield break; }
        }
        else
        {
            List<ZDO> chests;
            try
            {
                Check(Store!.ChestCount == 2, "Both unloaded chests restored from world save");
                ValidateItems(Decode(Store.Snapshot().Inventory));
                chests = Objects.Where(zdo => zdo.GetPrefab() == "GuildChest".GetStableHashCode()).ToList();
                Check(chests.Count == 2, "Two saved world objects");
                chests[0].SetOwner(ZNet.GetUID()); ZDOMan.instance.DestroyZDO(chests[0]);
            }
            catch (Exception exception) { Fail(exception); yield break; }
            yield return new WaitForSeconds(3);
            try
            {
                Check(Store!.ChestCount == 1, "First chest deletion acknowledged");
                ValidateItems(Decode(Store.Snapshot().Inventory));
                Check(!Objects.Any(zdo => zdo.GetString("gc_spill_id") != "" && zdo.GetPrefab() != "GuildChestWorldState".GetStableHashCode()), "No contents dropped before final removal");
                chests[1].SetOwner(ZNet.GetUID()); ZDOMan.instance.DestroyZDO(chests[1]);
            }
            catch (Exception exception) { Fail(exception); yield break; }
            yield return new WaitForSeconds(3);
            try
            {
                Check(Store!.ChestCount == 0, "Final chest deletion acknowledged");
                Check(Decode(Store.Snapshot().Inventory).NrOfItems() == 0, "Shared store cleared");
                var drops = Objects.Where(zdo => zdo.GetString("gc_spill_id") != "" && zdo.GetPrefab() != "GuildChestWorldState".GetStableHashCode()).ToList();
                Check(drops.Count == 2, "Exactly two item stacks dropped");
                var weapon = drops.Single(zdo => zdo.GetPrefab() == "SwordIron".GetStableHashCode());
                var packet = new ZPackage(weapon.GetByteArray(ZDOVars.s_itemData));
                int version = packet.ReadByte(); var (_, item) = ItemDrop.ItemData.Load(packet, (global::Version.Item)version);
                Check(item.m_quality == 3 && item.m_customData["fixture"] == "preserved" && item.m_crafterID == 12345, "Dropped metadata preserved");
                ZNet.instance.Save(true);
                File.AppendAllText(Marker, "phase2: world reload, unloaded chest retention, final spill and metadata passed\n");
                Logger.LogInfo("NATIVE_SMOKE_PHASE2_PASSED");
            }
            catch (Exception exception) { Fail(exception); yield break; }
        }
        yield return new WaitForSeconds(2); Application.Quit();
    }
    private static void ValidateItems(Inventory inventory)
    {
        Check(inventory.NrOfItems() == 2 && inventory.GetItemAt(0, 0).m_stack == 12, "Ore stack preserved");
        var weapon = inventory.GetItemAt(1, 0);
        Check(weapon.m_quality == 3 && Math.Abs(weapon.m_durability - 12.34f) < 0.011 && weapon.m_crafterName == "Fixture" && weapon.m_customData["fixture"] == "preserved", "Weapon metadata preserved");
    }
    private IEnumerator RunTransport()
    {
        yield return new WaitForSeconds(3);
        var client = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.Client")!;
        var action = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.QuickAction")!;
        bool HasSession() => (bool)client.GetProperty("HasSession", Flags)!.GetValue(null);
        bool Pending() => (bool)client.GetProperty("Pending", Flags)!.GetValue(null);
        Inventory View() => (Inventory)client.GetField("View", Flags)!.GetValue(null);
        void Select(InventoryGrid grid, ItemDrop.ItemData item, Vector2i position, InventoryGrid.Modifier modifier) =>
            client.GetMethod("Select", Flags)!.Invoke(null, new object[] { InventoryGui.instance, grid, item, position, modifier });
        Player player; Container chest;
        try
        {
            var position = new Vector3(0, 1000, 0);
            player = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("Player"), position, Quaternion.identity).GetComponent<Player>();
            player.enabled = false; player.GetComponent<Rigidbody>().isKinematic = true;
            player.SetPlayerID(987654, "NativeFixture"); Player.m_localPlayer = player; Player.m_localPlayerExists = true;
            // The dedicated scene has no normal player/cutscene input lifecycle. Bind grids explicitly.
            InventoryGui.instance.enabled = false;
            var ore = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>().m_itemData.Clone();
            ore.m_dropPrefab = ObjectDB.instance.GetItemPrefab("Iron"); ore.m_stack = 15; ore.m_gridPos = new Vector2i(0, 0);
            player.GetInventory().GetAllItems().Add(ore);
            var unknown = ObjectDB.instance.GetItemPrefab("SwordIron").GetComponent<ItemDrop>().m_itemData.Clone();
            unknown.m_dropPrefab = new GameObject("GuildChestClientOnlyFixture");
            unknown.m_gridPos = new Vector2i(7, 0); unknown.m_quality = 3; unknown.m_durability = 12.34f;
            unknown.m_customData["client-only"] = "preserved";
            player.GetInventory().GetAllItems().Add(unknown);
            Check(ObjectDB.instance.GetItemPrefab(unknown.m_dropPrefab.name.GetStableHashCode()) == null, "Fixture prefab absent from server database");
            ValidateUnresolvedItems(player.GetInventory());
            chest = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("GuildChest"), position + Vector3.right, Quaternion.identity).GetComponent<Container>();
            chest.GetComponent<WearNTear>().m_noSupportWear = false;
            chest.GetComponent<WearNTear>().m_noRoofWear = false;
            client.GetMethod("Open", Flags)!.Invoke(null, new object[] { chest, Enum.ToObject(action, 0) });
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Logger.LogInfo($"Transport diagnostic: session={HasSession()}, lease={Store!.ActiveLease?.Peer}, uid={ZNet.GetUID()}, dead={player.IsDead()}, cutscene={player.InCutscene()}, distance={Vector3.Distance(player.transform.position, chest.transform.position)}");
            Check(HasSession() && Store!.ActiveLease?.Peer == ZNet.GetUID(), "Host's open request and response travel through RPCs");
            Check(InventoryGui.instance.IsContainerOpen(), "Vanilla UI opened");
            typeof(InventoryGrid).GetField("m_inventory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(InventoryGui.instance.m_playerGrid, player.GetInventory());
            typeof(InventoryGrid).GetField("m_inventory", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(InventoryGui.instance.ContainerGrid, View());
            Select(InventoryGui.instance.m_playerGrid, player.GetInventory().GetItemAt(0, 0), new Vector2i(0, 0), InventoryGrid.Modifier.Move);
            if (Pending()) Check(player.GetInventory().GetItemAt(0, 0).m_stack == 15, "Real player inventory unchanged before acknowledgement");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(!Pending() && player.GetInventory().NrOfItems() == 1 && player.GetInventory().CountItems("$item_iron") == 0, "Deposit acknowledged with unrelated unresolved item retained");
            Check(Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == 15, "Deposit shared on host");
            client.GetMethod("Bulk", Flags)!.Invoke(null, new object[] { true });
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(!Pending() && player.GetInventory().CountItems("$item_iron") == 15 && Decode(Store!.Snapshot().Inventory).NrOfItems() == 0, "Take All acknowledged without duplicate stacks");
            var ore = player.GetInventory().GetAllItems().Single(item => item.m_dropPrefab.name == "Iron");
            typeof(InventoryGui).GetMethod("SetupDragItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(InventoryGui.instance, new object[] { ore, player.GetInventory(), 5 });
            Select(InventoryGui.instance.ContainerGrid, null!, new Vector2i(0, 0), InventoryGrid.Modifier.Select);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(!Pending() && player.GetInventory().CountItems("$item_iron") == 10 && Decode(Store!.Snapshot().Inventory).GetItemAt(0, 0).m_stack == 5, "Partial drag deposit");
            var ore = View().GetItemAt(0, 0);
            typeof(InventoryGui).GetMethod("SetupDragItem", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(InventoryGui.instance, new object[] { ore, View(), 2 });
            Select(InventoryGui.instance.m_playerGrid, null!, new Vector2i(1, 0), InventoryGrid.Modifier.Select);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(!Pending() && player.GetInventory().CountItems("$item_iron") == 12 && Decode(Store!.Snapshot().Inventory).GetItemAt(0, 0).m_stack == 3, "Partial drag withdrawal");
            client.GetMethod("Bulk", Flags)!.Invoke(null, new object[] { false });
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(!Pending() && player.GetInventory().NrOfItems() == 1 && Decode(Store!.Snapshot().Inventory).GetItemAt(0, 0).m_stack == 15, "Stack All acknowledged with unresolved item retained");
            var unknown = player.GetInventory().GetAllItems().Single();
            Check(unknown.m_dropPrefab.name == "GuildChestClientOnlyFixture" && unknown.m_quality == 3 && unknown.m_customData["client-only"] == "preserved", "Unrelated item's metadata retained throughout transfers");
            InventoryGui.instance.Hide();
            Check(!HasSession(), "Close clears client session");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(Store!.ActiveLease == null, "Close request releases host lease");
            File.WriteAllText(Marker, "host transport: unresolved player prefab retained; open, UI, staged quick deposit, Take All, partial drag deposit/withdrawal, Stack All, close passed\n");
            Logger.LogInfo("NATIVE_SMOKE_TRANSPORT_PASSED"); Application.Quit();
        }
        catch (Exception exception) { Fail(exception); }
    }
    private void ValidateUnresolvedItems(Inventory player)
    {
        Inventory Clone(Inventory source) => (Inventory)codec.GetMethod("Clone", Flags)!.Invoke(null, new object[] { source });
        var empty = new Inventory("fixture", null, 8, 4);
        var after = Clone(player);
        var unknown = after.GetItemAt(7, 0);
        unknown.m_gridPos = new Vector2i(6, 0);
        Check(Conserves(empty, player, empty, after), "Unknown player prefab permits local rearrangement");
        unknown.m_customData["client-only"] = "changed";
        Check(!Conserves(empty, player, empty, after), "Unknown item metadata mutation rejected");
        unknown.m_customData["client-only"] = "preserved";
        unknown.m_stack = 2;
        Check(!Conserves(empty, player, empty, after), "Unknown item quantity mutation rejected");
        bool rejected = false;
        try { Decode(Encode(player)); }
        catch (TargetInvocationException exception) { rejected = exception.InnerException is InvalidOperationException && exception.InnerException.Message.Contains("Missing shared-storage item prefab"); }
        Check(rejected, "Unresolved items still rejected in shared storage");
        var invalid = Clone(player);
        invalid.GetItemAt(7, 0).m_gridPos = new Vector2i(0, 0);
        rejected = false;
        try { Conserves(empty, player, empty, invalid); }
        catch (TargetInvocationException exception) { rejected = exception.InnerException is InvalidOperationException; }
        Check(rejected, "Duplicate player slots rejected without resolving prefabs");
        Logger.LogInfo("NATIVE_SMOKE_UNRESOLVED_PREFAB_PASSED");
    }
    private void Fail(Exception exception)
    {
        Logger.LogError("NATIVE_SMOKE_FAILED: " + exception);
        File.WriteAllText(Marker + ".failed", exception.ToString()); Application.Quit();
    }
}
