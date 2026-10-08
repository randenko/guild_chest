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
public sealed partial class NativeSmokeHarness : BaseUnityPlugin
{
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private readonly Type host = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.Host")!;
    private readonly Type codec = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.InventoryCodec")!;
    private bool ran;
    private static NativeSmokeHarness fixture = null!;
    private static bool delivering;
    private static int transferEffects;
    private static bool portalPlacement;
    private static bool clientAuthorityTick;
    private static int buildAgainMessages;
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
        fixture = this;
        if (Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE_TRANSPORT") == "1")
            new Harmony("com.randenko.guildchest.smoke.keepalive").Patch(AccessTools.Method(typeof(ZNetScene), "RemoveObjects"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(KeepFixtureObjects)));
        if (Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE_COMPAT") == "1")
        {
            new Harmony("com.randenko.guildchest.smoke.delay").Patch(AccessTools.Method(typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.Client"), "Handle"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(DelayCommitReply)));
            new Harmony("com.randenko.guildchest.smoke.effects").Patch(AccessTools.Method(typeof(EffectList), "Create"), postfix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(ObserveTransferEffect)));
            new Harmony("com.randenko.guildchest.smoke.placement").Patch(AccessTools.Method(typeof(Player), "UpdatePlacementGhost"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(KeepPortalPlacement)));
            new Harmony("com.randenko.guildchest.smoke.ghost").Patch(AccessTools.Method(typeof(Player), "SetupPlacementGhost"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(KeepPortalPlacement)));
            new Harmony("com.randenko.guildchest.smoke.portalspawn").Patch(AccessTools.Method(typeof(Player), "PlacePiece"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(PlaceHeadlessPortal)));
            new Harmony("com.randenko.guildchest.smoke.clientrole").Patch(AccessTools.Method(typeof(ZNet), "IsServer"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(ClientRoleForAuthorityTick)));
            new Harmony("com.randenko.guildchest.smoke.messages").Patch(AccessTools.Method(typeof(Character), "Message"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(ObserveBuildMessage)));
        }
    }
    private static void ObserveBuildMessage(object[] __args)
    {
        if (__args.OfType<string>().Any(text => text.Contains("Build again") || text.Contains("Guild materials are ready"))) buildAgainMessages++;
    }
    private static bool ClientRoleForAuthorityTick(ref bool __result)
    {
        if (!clientAuthorityTick) return true;
        __result = false; return false;
    }
    private static bool KeepPortalPlacement(Player __instance) => !portalPlacement || __instance != Player.m_localPlayer;
    private static bool PlaceHeadlessPortal(Player __instance, Piece piece, Vector3 pos, Quaternion rot)
    {
        if (!portalPlacement || __instance != Player.m_localPlayer) return true;
        // Dedicated servers have no DistributionPlatform.LocalUser, which native
        // PlacePiece needs for creator metadata. Instantiate the actual prefab only
        // after the real TryPlacePiece body reaches placement in this fixture.
        UnityEngine.Object.Instantiate(piece.gameObject, pos, rot);
        return false;
    }
    private static void ObserveTransferEffect(EffectList __instance)
    {
        if (InventoryGui.instance && __instance == InventoryGui.instance.m_moveItemEffects) transferEffects++;
    }
    private static bool DelayCommitReply(long sender, ZPackage package)
    {
        if (delivering) return true;
        int operation = new ZPackage(package.GetArray()).ReadInt();
        if (operation == 1) lastCommitReply = package.GetArray();
        if (operation == 5) lastOpenReply = package.GetArray();
        if (operation == 1 && dropNextCommitReply) { dropNextCommitReply = false; return false; }
        if (operation == 5 && dropNextOpenReply) { dropNextOpenReply = false; return false; }
        if (operation == 4 && dropNextPeek) { dropNextPeek = false; return false; }
        float delay = operation == 1 ? 0.5f : operation == 4 ? peekDelay : operation == 5 ? openDelay : 0f;
        if (delay <= 0) return true;
        fixture.StartCoroutine(fixture.DeliverReply(sender, package.GetArray(), delay)); return false;
    }
    private IEnumerator DeliverReply(long sender, byte[] bytes, float delay)
    {
        yield return new WaitForSeconds(delay);
        try
        {
            delivering = true;
            AccessTools.Method(typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.Client"), "Handle").Invoke(null, new object[] { sender, new ZPackage(bytes) });
        }
        finally { delivering = false; }
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
                Check(piece.m_craftingStation.name.StartsWith("piece_workbench"), "Workbench recipe");
                var costs = piece.m_resources.ToDictionary(req => req.m_resItem.name, req => req.m_amount);
                Check(costs.Count == 4 && costs["FineWood"] == 20 && costs["Iron"] == 10 &&
                    costs["SurtlingCore"] == 2 && costs["Coins"] == 250, "Recipe costs include 250 gold");
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
            // Prevent the headless Game.Update spawn lifecycle from replacing or
            // teleporting the synthetic player once world generation completes.
            Game.instance.enabled = false;
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
            unknown.m_gridPos = new Vector2i(7, 0); unknown.m_quality = 3; unknown.m_durability = 12.34f; unknown.m_equipped = true;
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
            if (Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE_COMPAT") != "1")
            {
            File.WriteAllText(Marker, "host transport: unresolved player prefab retained; open, UI, staged quick deposit, Take All, partial drag deposit/withdrawal, Stack All, close passed\n");
            Logger.LogInfo("NATIVE_SMOKE_TRANSPORT_PASSED"); Application.Quit();
            }
        }
        catch (Exception exception) { Fail(exception); }
        if (Environment.GetEnvironmentVariable("GUILDCHEST_SMOKE_COMPAT") == "1") yield return RunCompatibility(player, chest);
    }
    private IEnumerator RunCompatibility(Player player, Container chest)
    {
        var assembly = typeof(GuildChest.Plugin).Assembly;
        var compat = assembly.GetType("GuildChest.StoragePreview")!;
        bool Pending() => (bool)assembly.GetType("GuildChest.Client")!.GetProperty("Pending", Flags)!.GetValue(null);
        var auto = BepInEx.Bootstrap.Chainloader.PluginInfos["Azumatt.AzuAutoStore"].Instance.GetType().Assembly;
        var crafty = BepInEx.Bootstrap.Chainloader.PluginInfos["Azumatt.AzuCraftyBoxes"].Instance.GetType().Assembly;
        var autoFunctions = auto.GetType("AzuAutoStore.Util.Functions")!;
        var craftyWrapper = crafty.GetType("AzuCraftyBoxes.IContainers.VanillaContainer")!;
        var ordinary = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("piece_chest"), player.transform.position + Vector3.left, Quaternion.identity).GetComponent<Container>();
        var second = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("GuildChest"), player.transform.position + Vector3.forward, Quaternion.identity).GetComponent<Container>();
        bool? completed = null;
        int effectsBefore = 0;
        Piece buildPiece = null!;
        void CallAutoStore() => AccessTools.Method(autoFunctions, "TryStore", Type.EmptyTypes).Invoke(null, null);
        try
        {
            ordinary.GetComponent<WearNTear>().m_noSupportWear = false; ordinary.GetComponent<WearNTear>().m_noRoofWear = false;
            second.GetComponent<WearNTear>().m_noSupportWear = false; second.GetComponent<WearNTear>().m_noRoofWear = false;
            chest.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_creator, player.GetPlayerID());
            second.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_creator, player.GetPlayerID());
            var autoPlugin = auto.GetType("AzuAutoStore.AzuAutoStorePlugin")!;
            var setting = AccessTools.Field(autoPlugin, "MustHaveExistingItemToPull").GetValue(null);
            setting.GetType().GetProperty("Value")!.SetValue(setting, Enum.ToObject(autoPlugin.GetNestedType("Toggle")!, 0));
            AccessTools.Method(auto.GetType("AzuAutoStore.Util.Boxes"), "AddContainer").Invoke(null, new object[] { chest });
            AccessTools.Method(auto.GetType("AzuAutoStore.Util.Boxes"), "AddContainer").Invoke(null, new object[] { second });
            AccessTools.Method(crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes"), "AddContainer").Invoke(null, new object[] { chest });
            AccessTools.Method(crafty.GetType("AzuCraftyBoxes.Util.Functions.Boxes"), "AddContainer").Invoke(null, new object[] { second });
            VerifyAdapterBindings(player, chest, second);
            Check(chest.GetInventory().GetEmptySlots() == 0, "Unadapted guild inventory exposes no writable capacity");
            var ore = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>().m_itemData.Clone();
            ore.m_dropPrefab = ObjectDB.instance.GetItemPrefab("Iron");
            Check(!chest.GetInventory().AddItem(ore), "Direct guild deposit rejected without mutating source");
            Check(ordinary.GetInventory().AddItem(ore.Clone()), "Ordinary chest remains writable");
            ordinary.GetInventory().RemoveAll();
            Logger.LogInfo($"API diagnostic: guild={GuildChestStorage.IsGuildChest(chest)}, local={Player.m_localPlayer == player}, dead={player.IsDead()}, teleporting={player.IsTeleporting()}, session={assembly.GetType("GuildChest.Client")!.GetProperty("HasSession", Flags)!.GetValue(null)}, opening={assembly.GetType("GuildChest.Client")!.GetProperty("Opening", Flags)!.GetValue(null)}");
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) => personal.MoveAll(shared), (ok, _) => completed = ok), "API withdrawal starts");
            if (Pending()) Check(player.GetInventory().CountItems("$item_iron") == 0, "API keeps real player unchanged before ACK");
            Check(Pending() && !player.GetInventory().AddItem(ore.Clone()), "Player API writes blocked during delayed acknowledgement");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(completed == true && player.GetInventory().CountItems("$item_iron") == 15, "API withdrawal acknowledged");
            var ore = player.GetInventory().GetAllItems().Single(item => item.m_dropPrefab.name == "Iron");
            ore.m_gridPos = new Vector2i(0, 0);
            effectsBefore = transferEffects;
            CallAutoStore();
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(player.GetInventory().CountItems("$item_iron") == 15 && Decode(Store!.Snapshot().Inventory).NrOfItems() == 0, "Actual AzuAutoStore preserves hotbar exclusion");
            Check(transferEffects == effectsBefore, "No transfer effect for a skipped deposit");
            var ore = player.GetInventory().GetAllItems().Single(item => item.m_dropPrefab.name == "Iron");
            ore.m_gridPos = new Vector2i(0, 1);
            CallAutoStore();
            if (Pending()) Check(player.GetInventory().CountItems("$item_iron") == 15, "AzuAutoStore hotkey path leaves real items pending ACK");
            Check(transferEffects == effectsBefore, "Transfer effect waits for acknowledgement");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(player.GetInventory().CountItems("$item_iron") == 0 && Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == 15, "Actual AzuAutoStore deposits once across two guild chests");
            Check(transferEffects == effectsBefore + 1, "Exactly one guild transfer effect after acknowledgement");
            Check(chest.GetComponent(auto.GetType("AzuAutoStore.Util.ChestPingEffect")) || second.GetComponent(auto.GetType("AzuAutoStore.Util.ChestPingEffect")), "AutoStore's configured chest ping created");
            Check(chest.GetComponent(auto.GetType("AzuAutoStore.Util.HighLightChest")) || second.GetComponent(auto.GetType("AzuAutoStore.Util.HighLightChest")), "AutoStore's configured highlight created");
            var a = AccessTools.Method(craftyWrapper, "Create").Invoke(null, new object[] { chest });
            var b = AccessTools.Method(craftyWrapper, "Create").Invoke(null, new object[] { second });
            ScopesType.GetField("PreviewDepth", Flags)!.SetValue(null, 1);
            int count = (int)AccessTools.Method(craftyWrapper, "ItemCount").Invoke(a, new object[] { "$item_iron" }) +
                (int)AccessTools.Method(craftyWrapper, "ItemCount").Invoke(b, new object[] { "$item_iron" });
            ScopesType.GetField("PreviewDepth", Flags)!.SetValue(null, 0);
            Check(count == 15, "Crafty counts shared resources once across nearby guild aliases");
            buildPiece = new GameObject("GuildChestSmokeBuild").AddComponent<Piece>();
            buildPiece.m_name = "GuildChestSmokeBuild"; buildPiece.m_description = "Fixture";
            buildPiece.m_resources = new[] { new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>(), m_amount = 4 } };
            // Deliberately populate the same-frame UI cache outside a preview first.
            var bank = crafty.GetType("AzuCraftyBoxes.Util.Functions.UiItemBank")!;
            AccessTools.Method(bank, "GetTotalAnyQuality").Invoke(null, new object[] { "$item_iron" });
            AccessTools.Method(typeof(Hud), "SetupPieceInfo").Invoke(Hud.instance, new object[] { buildPiece });
            var selection = AccessTools.Field(typeof(Hud), "m_buildSelection").GetValue(Hud.instance);
            string label = (string)selection.GetType().GetProperty("text")!.GetValue(selection);
            Logger.LogInfo("Build HUD diagnostic: " + label);
            Check(label.Contains(">3</color>"), "Build HUD shows three buildable pieces from guild-only stock");
            Check(player.HaveRequirements(buildPiece, Player.RequirementMode.CanBuild), "Actual hammer CanBuild gate sees guild-only materials");
            // Exercise generic resource staging separately; the real hammer loop
            // and automatic continuation are exercised with the portal below.
            SupplyType.GetMethod("Supply", Flags)!.Invoke(null, new object[] { buildPiece.m_resources, 0, 1, false, true, (Action)(() => { }) });
            if (Pending()) Check(player.GetInventory().CountItems("$item_iron") == 0, "Building materials remain staged pending ACK");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        Recipe recipe = null!;
        try
        {
            Check(player.GetInventory().CountItems("$item_iron") == 4 && Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == 11, "Building material supply acknowledged and conserved");
            Check(player.HaveRequirements(buildPiece, Player.RequirementMode.CanBuild), "Hammer CanBuild gate still passes after acknowledged supply");
            player.ConsumeResources(new[] { new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>(), m_amount = 4 } }, 0);
            Check(player.GetInventory().CountItems("$item_iron") == 0 && Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == 11, "Actual Crafty ConsumeResources consumes only acknowledged player supply");
            recipe = ScriptableObject.CreateInstance<Recipe>(); recipe.name = "GuildChestSmokeRecipe";
            recipe.m_item = ObjectDB.instance.GetItemPrefab("SwordIron").GetComponent<ItemDrop>(); recipe.m_amount = 1; recipe.m_enabled = true;
            recipe.m_resources = new[] { new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>(), m_amount = 4 } };
            AccessTools.Field(typeof(InventoryGui), "m_craftRecipe").SetValue(InventoryGui.instance, recipe);
            AccessTools.Field(typeof(InventoryGui), "m_craftUpgradeItem").SetValue(InventoryGui.instance, null);
            AccessTools.Method(typeof(InventoryGui), "DoCrafting").Invoke(InventoryGui.instance, new object[] { player });
            if (Pending()) Check(!player.GetInventory().GetAllItems().Any(item => item.m_dropPrefab.name == "SwordIron"), "Crafted output waits for material ACK");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(player.GetInventory().GetAllItems().Count(item => item.m_dropPrefab.name == "SwordIron") == 1 &&
                player.GetInventory().CountItems("$item_iron") == 0 && Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == 7,
                "Actual DoCrafting resumes after ACK and consumes four guild iron once");
            completed = null;
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) => shared.GetAllItems()[0].m_stack++, (ok, _) => completed = ok), "Invalid transfer starts staged");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(completed == false && Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == 7, "Rejected transfer changes neither shared inventory nor player");
            Store!.Open(ZNet.GetUID(), second.GetComponent<ZNetView>().GetZDO().m_uid.ToString(), Time.unscaledTime, out var lease);
            completed = null;
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) => personal.MoveAll(shared), (ok, _) => completed = ok), "Busy transfer request starts");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(completed == false && player.GetInventory().CountItems("$item_iron") == 0 && Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == 7, "Busy lease denies automation without item loss");
            Store!.Close(ZNet.GetUID(), Store.ActiveLease!.Token);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return RunStorageRegressions(player, chest, ordinary, second);
        if (!File.Exists(Marker + ".failed")) yield return RunTransactionRegressions(player, chest, ordinary, second);
        if (!File.Exists(Marker + ".failed")) yield return RunPortal(player, chest);
    }
    private IEnumerator RunPortal(Player player, Container chest)
    {
        var compat = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.StoragePreview")!;
        var portal = ZNetScene.instance.GetPrefab("portal_wood").GetComponent<Piece>();
        var hash = "portal_wood".GetStableHashCode();
        int before = Objects.Count(zdo => zdo.GetPrefab() == hash);
        bool? completed = null;
        try
        {
            Check(portal.m_resources.Length == 3, "Real portal has three resource types");
            var workbench = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("piece_workbench"), player.transform.position + Vector3.right * 3, Quaternion.identity);
            workbench.GetComponent<WearNTear>().m_noSupportWear = false; workbench.GetComponent<WearNTear>().m_noRoofWear = false;
            var station = workbench.GetComponent<CraftingStation>();
            ((Dictionary<string, int>)AccessTools.Field(typeof(Player), "m_knownStations").GetValue(player))[station.m_name] = 1;
            foreach (var requirement in portal.m_resources)
            {
                var item = requirement.m_resItem.m_itemData.Clone(); item.m_dropPrefab = requirement.m_resItem.gameObject; item.m_stack = requirement.m_amount;
                Check(player.GetInventory().AddItem(item), "Seed portal material in disposable player");
            }
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) =>
            {
                foreach (var requirement in portal.m_resources)
                    foreach (var item in personal.GetAllItems().Where(item => item.m_shared.m_name == requirement.m_resItem.m_itemData.m_shared.m_name).ToList())
                        shared.MoveItemToThis(personal, item);
            }, (ok, _) => completed = ok), "Seed portal stock through acknowledged transfer");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(completed == true, "Portal stock deposit acknowledged");
            VerifyClientPreviewLifecycle(compat);
            foreach (var requirement in portal.m_resources) Check(player.GetInventory().CountItems(requirement.m_resItem.m_itemData.m_shared.m_name) == 0, "Portal materials only in guild stock");
            var crafty = BepInEx.Bootstrap.Chainloader.PluginInfos["Azumatt.AzuCraftyBoxes"].Instance.GetType().Assembly;
            var bank = crafty.GetType("AzuCraftyBoxes.Util.Functions.UiItemBank")!;
            foreach (var requirement in portal.m_resources)
                AccessTools.Method(bank, "GetTotalAnyQuality").Invoke(null, new object[] { requirement.m_resItem.m_itemData.m_shared.m_name });
            AccessTools.Method(typeof(Hud), "SetupPieceInfo").Invoke(Hud.instance, new object[] { portal });
            var selection = AccessTools.Field(typeof(Hud), "m_buildSelection").GetValue(Hud.instance);
            string label = (string)selection.GetType().GetProperty("text")!.GetValue(selection);
            Logger.LogInfo("Portal HUD diagnostic: " + label);
            Check(label.Contains(">1</color>"), "Portal HUD shows one buildable portal from guild-only stock");
            var rows = (GameObject[])AccessTools.Field(typeof(Hud), "m_requirementItems").GetValue(Hud.instance);
            for (int i = 0; i < portal.m_resources.Length; i++)
            {
                var text = rows[i].transform.Find("res_amount").GetComponents<Component>().First(component => component && component.GetType().GetProperty("text") != null);
                string value = (string)text.GetType().GetProperty("text")!.GetValue(text);
                int amount = portal.m_resources[i].m_amount;
                Logger.LogInfo($"Portal resource diagnostic: {portal.m_resources[i].m_resItem.name} = {value}");
                Check(value == $"{amount}/{amount}", "Portal HUD shows correct available and required amounts");
            }
            Check(player.HaveRequirements(portal, Player.RequirementMode.CanBuild), "Actual portal hammer resource and station gate passes");
            ConfigurePortalBuild(player, portal);
            Check(!player.TryPlacePiece(portal), "Portal placement waits for materials");
            var originalGhost = (GameObject)AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(player);
            var recreated = new GameObject("GuildChestRecreatedGhostFixture"); recreated.AddComponent<Piece>();
            recreated.transform.SetPositionAndRotation(originalGhost.transform.position, originalGhost.transform.rotation);
            AccessTools.Field(typeof(Player), "m_placementGhost").SetValue(player, recreated);
            Check(Objects.Count(zdo => zdo.GetPrefab() == hash) == before, "No portal created before material acknowledgement");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            foreach (var requirement in portal.m_resources) Check(player.GetInventory().CountItems(requirement.m_resItem.m_itemData.m_shared.m_name) == requirement.m_amount, "All portal materials acknowledged exactly once");
            float durability = player.RightItem.m_durability;
            float stamina = player.GetStamina();
            var continuation = typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.BuildingContinuation")!;
            var intent = continuation.GetField("pending", Flags)!.GetValue(null);
            Logger.LogInfo($"Continuation diagnostic: selected={player.GetSelectedPiece()?.name}, mode={player.InPlaceMode()}, menu={Hud.IsPieceSelectionVisible()}, ghost={((GameObject)AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(player)).activeInHierarchy}, pending={intent != null}, ready={intent?.GetType().GetField("Ready", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(intent)}");
            Logger.LogInfo($"Continuation validation: matches={continuation.GetMethod("Matches", Flags)!.Invoke(null, new[] { intent })}, stamina={stamina}, tool={player.RightItem.m_dropPrefab.name}, requirements={player.HaveRequirements(portal, Player.RequirementMode.CanBuild)}, time={Time.time}, lastTool={AccessTools.Field(typeof(Player), "m_lastToolUseTime").GetValue(player)}");
            object IntentField(string name) => intent!.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(intent);
            var liveGhost = (GameObject)AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(player);
            Logger.LogInfo($"Continuation identity: player={ReferenceEquals(IntentField("Player"), player)}, piece={ReferenceEquals(IntentField("Piece"), portal)}, tool={ReferenceEquals(IntentField("Tool"), player.RightItem)}, dead={player.IsDead()}, teleporting={player.IsTeleporting()}, playerDelta={Vector3.Distance(player.transform.position, (Vector3)IntentField("PlayerPosition"))}, targetDelta={Vector3.Distance(liveGhost.transform.position, (Vector3)IntentField("Position"))}, angle={Quaternion.Angle(liveGhost.transform.rotation, (Quaternion)IntentField("Rotation"))}, age={Time.unscaledTime - (float)IntentField("Started")}");
            // No second TryPlacePiece or synthetic click: only the ordinary next
            // update, which the acknowledged continuation schedules internally.
            AccessTools.Method(typeof(Player), "UpdatePlacement").Invoke(player, new object[] { true, 0.02f });
            Logger.LogInfo($"Continuation result: pending={continuation.GetField("pending", Flags)!.GetValue(null) != null}, replay={continuation.GetField("replaying", Flags)!.GetValue(null)}, pressed={AccessTools.Field(typeof(Player), "m_placePressedTime").GetValue(player)}, status={player.GetPlacementStatus()}, portals={Objects.Count(zdo => zdo.GetPrefab() == hash)}");
            Check(Objects.Count(zdo => zdo.GetPrefab() == hash) == before + 1, "Exactly one portal placed");
            foreach (var requirement in portal.m_resources) Check(player.GetInventory().CountItems(requirement.m_resItem.m_itemData.m_shared.m_name) == 0, "Placed portal consumes acknowledged resources once");
            Check(player.RightItem.m_durability < durability && player.GetStamina() < stamina, "Native build loop charges hammer durability and stamina");
            AccessTools.Method(typeof(Player), "UpdatePlacement").Invoke(player, new object[] { true, 0.02f });
            Check(Objects.Count(zdo => zdo.GetPrefab() == hash) == before + 1, "Following update does not replay the completed click");
            Check(buildAgainMessages == 0, "No resource-ready or re-click message displayed");
            Check((int)ScopesType.GetField("PreviewDepth", Flags)!.GetValue(null) == 0, "Preview scopes balanced after build checks");
            foreach (var requirement in portal.m_resources)
            {
                var item = requirement.m_resItem.m_itemData.Clone(); item.m_dropPrefab = requirement.m_resItem.gameObject; item.m_stack = requirement.m_amount;
                Check(player.GetInventory().AddItem(item), "Seed materials for cancellation check");
            }
            completed = null;
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) =>
            {
                foreach (var requirement in portal.m_resources)
                    foreach (var item in personal.GetAllItems().Where(item => item.m_shared.m_name == requirement.m_resItem.m_itemData.m_shared.m_name).ToList()) shared.MoveItemToThis(personal, item);
            }, (ok, _) => completed = ok), "Seed cancellation stock");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            Check(completed == true, "Cancellation stock acknowledged");
            Check(!player.TryPlacePiece(portal), "Changed-aim build starts staged");
            var ghost = (GameObject)AccessTools.Field(typeof(Player), "m_placementGhost").GetValue(player);
            ghost.transform.position += Vector3.right;
            AccessTools.Method(typeof(Player), "UpdatePlacement").Invoke(player, new object[] { true, 0.02f });
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            AccessTools.Method(typeof(Player), "UpdatePlacement").Invoke(player, new object[] { true, 0.02f });
            Check(Objects.Count(zdo => zdo.GetPrefab() == hash) == before + 1, "Changed aim cancels deferred placement");
            foreach (var requirement in portal.m_resources) Check(player.GetInventory().CountItems(requirement.m_resItem.m_itemData.m_shared.m_name) == requirement.m_amount, "Cancelled placement retains acknowledged materials");
            Check(buildAgainMessages == 0, "Cancelled build does not request a re-click");
            File.WriteAllText(Marker, "compatibility: transaction application/cancellation/scopes; ingredient-quality; AutoStore continuation; slow/stale/expired Peek; automatic single-click portal build; materials/stamina/durability charged once; no re-click message; changed-aim cancellation; client preview lifecycle; effects; crafting; rejection/busy checks passed\n");
            Logger.LogInfo("NATIVE_SMOKE_PORTAL_PASSED"); Logger.LogInfo("NATIVE_SMOKE_COMPAT_PASSED"); Application.Quit();
        }
        catch (Exception exception) { Fail(exception); }
    }
    private static void ConfigurePortalBuild(Player player, Piece portal)
    {
        var hammer = ObjectDB.instance.GetItemPrefab("Hammer").GetComponent<ItemDrop>().m_itemData.Clone();
        hammer.m_dropPrefab = ObjectDB.instance.GetItemPrefab("Hammer"); hammer.m_equipped = true;
        Check(player.GetInventory().AddItem(hammer), "Fixture hammer added");
        AccessTools.Field(typeof(Humanoid), "m_rightItem").SetValue(player, hammer);
        var table = new GameObject("GuildChestFixtureBuildTable").AddComponent<PieceTable>();
        table.m_pieces.Add(portal.gameObject);
        table.m_categories.Add(portal.m_category); table.m_availablePieces.Add(portal);
        AccessTools.Field(typeof(PieceTable), "m_availablePiecesByCategory").SetValue(table, Enumerable.Range(0, 9).Select(_ => new List<Piece> { portal }).ToList());
        AccessTools.Field(typeof(PieceTable), "m_selectedCategory").SetValue(table, portal.m_category);
        AccessTools.Field(typeof(Player), "m_buildPieces").SetValue(player, table);
        var ghost = new GameObject("GuildChestPortalPlacementFixture"); ghost.AddComponent<Piece>();
        ghost.transform.position = player.transform.position + Vector3.forward * 5;
        AccessTools.Field(typeof(Player), "m_placementGhost").SetValue(player, ghost);
        AccessTools.Field(typeof(Player), "m_placementStatus").SetValue(player, Player.PlacementStatus.Valid);
        AccessTools.Field(typeof(Player), "m_placePressedTime").SetValue(player, -9999f);
        AccessTools.Field(typeof(Player), "m_lastToolUseTime").SetValue(player, -9999f);
        ((Component)AccessTools.Field(typeof(Hud), "m_buildUi").GetValue(Hud.instance)).gameObject.SetActive(false);
        portalPlacement = true;
        player.AddStamina(100);
    }
    private void VerifyClientPreviewLifecycle(Type compat)
    {
        var snapshotField = compat.GetField("snapshot", Flags)!;
        var snapshotChestField = compat.GetField("snapshotChest", Flags)!;
        var originalSnapshot = snapshotField.GetValue(null);
        var originalChest = snapshotChestField.GetValue(null);
        Check(originalSnapshot != null && originalChest != null, "Preview populated before client frame checks");
        var storeField = host.GetField("Store", Flags)!;
        var managerField = host.GetField("manager", Flags)!;
        var stateField = host.GetField("state", Flags)!;
        var originalStore = storeField.GetValue(null); var originalManager = managerField.GetValue(null); var originalState = stateField.GetValue(null);
        try
        {
            // Match the remote client's real authority state: no Store or manager,
            // but an active ZDOMan and an asynchronously received inventory preview.
            storeField.SetValue(null, null); managerField.SetValue(null, null); stateField.SetValue(null, null);
            clientAuthorityTick = true;
            for (int i = 0; i < 5; i++) host.GetMethod("Tick", Flags)!.Invoke(null, null);
            Check(ReferenceEquals(snapshotField.GetValue(null), originalSnapshot) &&
                ReferenceEquals(snapshotChestField.GetValue(null), originalChest), "Client authority updates preserve crafting preview");
            host.GetMethod("Reset", Flags)!.Invoke(null, null);
            Check(ReferenceEquals(snapshotField.GetValue(null), originalSnapshot), "Authority reset cannot erase client preview");
        }
        finally
        {
            clientAuthorityTick = false;
            storeField.SetValue(null, originalStore); managerField.SetValue(null, originalManager); stateField.SetValue(null, originalState);
        }
        Logger.LogInfo("NATIVE_SMOKE_CLIENT_PREVIEW_LIFECYCLE_PASSED");
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
