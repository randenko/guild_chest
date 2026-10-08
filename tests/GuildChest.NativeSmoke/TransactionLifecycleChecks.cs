using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using GuildChest.Core;
using HarmonyLib;
using UnityEngine;

namespace GuildChest.NativeSmoke;

public sealed partial class NativeSmokeHarness
{
    private static byte[]? lastCommitReply, lastOpenReply;
    private static bool dropNextCommitReply, dropNextOpenReply, failClone, failPreview, failScope;
    private static int commitRequests;
    private static bool HasClientSession => (bool)ClientType.GetProperty("HasSession", Flags)!.GetValue(null);
    private static void DeliverNow(byte[] bytes)
    {
        try { delivering = true; AccessTools.Method(ClientType, "Handle").Invoke(null, new object[] { ZNet.GetUID(), new ZPackage(bytes) }); }
        finally { delivering = false; }
    }
    private static void ObserveCommitRequest(ZPackage package)
    {
        if (new ZPackage(package.GetArray()).ReadInt() == 1) commitRequests++;
    }
    private static TransferPhase? Phase()
    {
        var transaction = AccessTools.Field(ClientType, "transaction").GetValue(null);
        return transaction == null ? null : ((TransferLifecycle)AccessTools.Field(transaction.GetType(), "Lifecycle").GetValue(transaction)).Phase;
    }
    private static void FailPreparation()
    {
        if (!failClone || Phase() != TransferPhase.Accepted) return;
        failClone = false; throw new InvalidOperationException("Lifecycle fixture: item preparation failure");
    }
    private static void FailPreviewRefresh()
    {
        if (!failPreview || !TransferPending || !(bool)AccessTools.Field(ClientType, "Applying").GetValue(null)) return;
        failPreview = false; throw new InvalidOperationException("Lifecycle fixture: preview refresh failure");
    }
    private static void FailBeforeScope()
    {
        if (!failScope) return;
        failScope = false; throw new InvalidOperationException("Lifecycle fixture: earlier Harmony prefix failure");
    }
    private static void MoveIron(Inventory personal, Inventory shared, int amount)
    {
        var source = shared.GetAllItems().Find(item => item.m_dropPrefab.name == "Iron");
        if (source == null || source.m_stack < amount) throw new InvalidOperationException("Lifecycle fixture guild iron unavailable");
        var copy = source.Clone(); copy.m_stack = amount;
        Check(personal.AddItem(copy), "Lifecycle fixture player capacity");
        shared.RemoveItem(source, amount);
    }
    private Container AccessPoint(Player player)
    {
        var chest = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("GuildChest"), player.transform.position + Vector3.right * 2, Quaternion.identity).GetComponent<Container>();
        chest.GetComponent<WearNTear>().m_noSupportWear = false; chest.GetComponent<WearNTear>().m_noRoofWear = false;
        chest.GetComponent<ZNetView>().GetZDO().Set(ZDOVars.s_creator, player.GetPlayerID());
        return chest;
    }
    private IEnumerator RunTransactionRegressions(Player player, Container chest, Container ordinary, Container second)
    {
        var faultHooks = new Harmony("com.randenko.guildchest.smoke.lifecycle");
        faultHooks.Patch(AccessTools.Method(host, "Handle"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(ObserveCommitRequest)));
        faultHooks.Patch(AccessTools.Method(ClientType.GetNestedType("Transaction", BindingFlags.NonPublic), "Apply"),
            prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(FailPreparation)));
        faultHooks.Patch(AccessTools.Method(PreviewType, "RefreshSnapshot"), prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(FailPreviewRefresh)));
        try
        {
            int callbacks = 0;
            Check(GuildChestStorage.TryTransfer(chest, (_, _) => { }, (ok, _) =>
            {
                callbacks++; Check(ok && !HasClientSession, "No-op closes automation session before completion");
                throw new InvalidOperationException("Lifecycle fixture: successful callback failure");
            }), "No-op starts");
            Check(callbacks == 1 && !TransferPending && !HasClientSession, "Throwing no-op callback receives exactly one outcome");
            callbacks = 0;
            Check(GuildChestStorage.TryTransfer(chest, (_, _) => throw new InvalidOperationException("Stage rejected"), (ok, _) =>
            {
                callbacks++; Check(!ok && !HasClientSession, "Staging failure cleans session before callback");
                throw new InvalidOperationException("Lifecycle fixture: rejected callback failure");
            }), "Rejected action starts");
            Check(callbacks == 1 && !HasClientSession, "Throwing rejection callback receives exactly one outcome");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        int callbacksAfterAck = 0;
        int personalBefore = player.GetInventory().CountItems("$item_iron");
        int sharedBefore = Decode(Store!.Snapshot().Inventory).CountItems("$item_iron");
        bool notificationFails = true;
        Action notify = () =>
        {
            if (!notificationFails) return;
            notificationFails = false; throw new InvalidOperationException("Lifecycle fixture: inventory notification failure");
        };
        try
        {
            player.GetInventory().m_onChanged += notify;
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) => MoveIron(personal, shared, 7), (ok, _) =>
            {
                Check(ok && !TransferPending && !HasClientSession, "Accepted notification-fault transfer finishes cleanup"); callbacksAfterAck++;
            }), "Notification-fault withdrawal starts");
        }
        catch (Exception exception) { player.GetInventory().m_onChanged -= notify; Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            player.GetInventory().m_onChanged -= notify;
            Check(callbacksAfterAck == 1 && player.GetInventory().CountItems("$item_iron") == personalBefore + 7 &&
                Decode(Store!.Snapshot().Inventory).CountItems("$item_iron") == sharedBefore - 7, "Notification failure withdraws seven iron exactly once");
            DeliverNow(lastCommitReply!);
            Check(callbacksAfterAck == 1 && player.GetInventory().CountItems("$item_iron") == personalBefore + 7, "Duplicate ACK cannot repeat mutation or completion");
            failPreview = true;
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) => MoveIron(personal, shared, 1), (ok, _) =>
            { Check(ok && !TransferPending, "Preview fault does not reject confirmed transfer"); callbacksAfterAck++; }), "Preview-fault transfer starts");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        int requestsBefore = 0;
        try
        {
            Check(!failPreview && callbacksAfterAck == 2 && player.GetInventory().CountItems("$item_iron") == personalBefore + 8 && !TransferPending, "Preview refresh failure cannot leave a replayable transfer");
            failClone = true; requestsBefore = commitRequests;
            Check(GuildChestStorage.TryTransfer(chest, (personal, shared) => MoveIron(personal, shared, 1), (ok, _) =>
            { Check(ok, "Local preparation retry accepted"); callbacksAfterAck++; }), "Preparation-fault transfer starts");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(1);
        try
        {
            Check(!failClone && Phase() == TransferPhase.Accepted && player.GetInventory().CountItems("$item_iron") == personalBefore + 8,
                "Preparation fails before live mutation and retains accepted outcome");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2);
        try
        {
            Check(callbacksAfterAck == 3 && player.GetInventory().CountItems("$item_iron") == personalBefore + 9 && commitRequests == requestsBefore + 1,
                "Accepted transfer retries application locally without sending another commit");
            Logger.LogInfo("NATIVE_SMOKE_TRANSACTION_APPLICATION_PASSED");
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return OpenCancellationRegressions(player, chest, ordinary, second);
        if (System.IO.File.Exists(Marker + ".failed")) yield break;
        try
        {
            var piece = new GameObject("GuildChestLifecycleRequirement").AddComponent<Piece>();
            piece.m_resources = new[] { new Piece.Requirement { m_resItem = ObjectDB.instance.GetItemPrefab("Iron").GetComponent<ItemDrop>(), m_amount = 1 } };
            void EarlierFault(MethodBase method, Action invoke, string counter)
            {
                faultHooks.Patch(method, prefix: new HarmonyMethod(typeof(NativeSmokeHarness), nameof(FailBeforeScope)) { priority = 900 });
                failScope = true;
                try { invoke(); } catch (Exception) { }
                Check(!failScope && (int)AccessTools.Field(ScopesType, counter).GetValue(null) == 0, "Earlier prefix failure leaves " + counter + " balanced");
            }
            EarlierFault(AccessTools.Method(typeof(Player), "HaveRequirements", new[] { typeof(Piece), typeof(Player.RequirementMode) }),
                () => player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "PreviewDepth");
            EarlierFault(AccessTools.Method(typeof(Player), "ConsumeResources"), () => player.ConsumeResources(Array.Empty<Piece.Requirement>(), 0), "ConsumptionDepth");
            EarlierFault(AccessTools.Method(typeof(InventoryGui), "UpdateContainerWeight"),
                () => AccessTools.Method(typeof(InventoryGui), "UpdateContainerWeight").Invoke(InventoryGui.instance, null), "UiDepth");
            Check(player.HaveRequirements(piece, Player.RequirementMode.CanBuild), "Build requirements still see guild stock after earlier prefix fault");
            faultHooks.UnpatchSelf();
            Logger.LogInfo("NATIVE_SMOKE_TRANSACTION_SCOPES_PASSED");
        }
        catch (Exception exception) { Fail(exception); }
    }
    private IEnumerator OpenCancellationRegressions(Player player, Container chest, Container ordinary, Container second)
    {
        int callbacks = 0;
        bool? result = null;
        byte[] personal = Encode(player.GetInventory());
        try
        {
            var temporary = AccessPoint(player); openDelay = 0.8f;
            Check(GuildChestStorage.TryTransfer(temporary, (inventory, shared) => MoveIron(inventory, shared, 1), (ok, _) => { callbacks++; result = ok; }), "Open through temporary chest starts");
            UnityEngine.Object.DestroyImmediate(temporary.gameObject);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(1.2f);
        try
        {
            Check(callbacks == 1 && result == false && !TransferPending && !(bool)ClientType.GetProperty("Opening", Flags)!.GetValue(null) &&
                personal.SequenceEqual(Encode(player.GetInventory())), "Destroyed opening chest completes cancellation once without moving items");
            Check(Store!.ActiveLease == null, "Late grant for destroyed chest released");
            var temporary = AccessPoint(player);
            ordinary.GetInventory().RemoveAll(); Add(ordinary.GetInventory(), "Iron", 1);
            AutoOrder(temporary, ordinary); AutoStore();
            UnityEngine.Object.DestroyImmediate(temporary.gameObject);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(1.2f);
        try
        {
            Check(player.GetInventory().CountItems("$item_iron") == 0 && ordinary.GetInventory().CountItems("$item_iron") == 10, "Canceled guild open resumes queued ordinary AutoStore deposit");
            Check(typeof(GuildChest.Plugin).Assembly.GetType("GuildChest.AutoStoreBatch")!.GetField("active", Flags)!.GetValue(null) == null, "AutoStore queue finishes after canceled open");
            openDelay = 0; callbacks = 0; dropNextOpenReply = true;
            Check(GuildChestStorage.TryTransfer(chest, (inventory, shared) => MoveIron(inventory, shared, 1), (ok, _) => { callbacks++; result = ok; }), "Dropped open starts");
            var request = AccessTools.Field(ClientType, "opening").GetValue(null);
            AccessTools.Field(request.GetType(), "Started").SetValue(request, Time.unscaledTime - 31f);
            AccessTools.Method(ClientType, "Tick").Invoke(null, null);
            Check(callbacks == 1 && result == false && !(bool)ClientType.GetProperty("Opening", Flags)!.GetValue(null), "Timeout completes opening request once");
            DeliverNow(lastOpenReply!);
            Check(callbacks == 1 && !HasClientSession && Store!.ActiveLease == null, "Timed-out grant ignored and released");
            callbacks = 0; dropNextOpenReply = true;
            Check(GuildChestStorage.TryTransfer(chest, (_, _) => { }, (ok, _) => { callbacks++; result = ok; }), "Reset cancellation starts");
            AccessTools.Method(ClientType, "Reset").Invoke(null, null); DeliverNow(lastOpenReply!);
            Check(callbacks == 1 && result == false && Store!.ActiveLease == null, "Reset cancels open once and late grant is released");
            AutoOrder(chest, second);
            Logger.LogInfo("NATIVE_SMOKE_TRANSACTION_CANCELLATION_PASSED");
        }
        catch (Exception exception) { Fail(exception); }
    }
}
