using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GuildChest.Core;
using HarmonyLib;
using UnityEngine;

namespace GuildChest;

internal static class Host
{
    private const string ItemsKey = "gc_inventory_v1";
    private const string RevisionKey = "gc_revision";
    private const string SpillKey = "gc_pending_spill";
    internal static SharedStore? Store;
    private static ZDO? state;
    private static ZDOMan? manager;
    private static float nextPeerCheck;
    private static string automationToken = "";
    private static readonly Dictionary<int, float> wardRadii = new();
    private static readonly HashSet<ZDOID> wards = new();
    internal static bool IsServer => ZNet.instance && ZNet.instance.IsServer();
    internal static bool IsState(ZDO zdo) => zdo.GetPrefab() == Plugin.StateHash;
    private static IEnumerable<ZDO> Objects => AccessTools.FieldRefAccess<ZDOMan, Dictionary<ZDOID, ZDO>>("m_objectsByID")(ZDOMan.instance).Values;

    internal static void Initialize()
    {
        if (!IsServer || ZNet.m_loadError || !ZNetScene.instance || !ZNetScene.instance.GetPrefab(Plugin.StateName)) return;
        if (manager == ZDOMan.instance && Store != null) return;
        manager = ZDOMan.instance;
        var records = Objects.Where(IsState).ToList();
        if (records.Count > 1) throw new InvalidOperationException("Multiple Guild Chest world state records; refusing to overwrite them.");
        state = records.SingleOrDefault() ?? manager.CreateNewZDO(Vector3.zero, Plugin.StateHash);
        state.Persistent = true; state.Type = ZDO.ObjectType.Solid; state.SetPrefab(Plugin.StateHash); state.SetOwner(ZNet.GetUID());
        int schema = state.GetInt("gc_schema", 1);
        if (schema != 1) throw new InvalidOperationException($"Unsupported Guild Chest save schema {schema}.");
        var empty = InventoryCodec.Save(InventoryCodec.Empty());
        Store = new SharedStore(empty, state.GetByteArray(ItemsKey, empty), state.GetLong(RevisionKey));
        wardRadii.Clear(); wards.Clear();
        var prefabs = AccessTools.FieldRefAccess<ZNetScene, Dictionary<int, GameObject>>("m_namedPrefabs")(ZNetScene.instance);
        foreach (var pair in prefabs)
        {
            var area = pair.Value.GetComponent<PrivateArea>();
            if (area) wardRadii[pair.Key] = area.m_radius;
        }
        foreach (var zdo in Objects) Register(zdo);
        Persist(); ResumeSpill();
        Plugin.LogInfo($"Guild Chest authority ready: {Store.ChestCount} chests; Valheim {(global::Version.GetVersionString())}.");
    }

    internal static void Persist()
    {
        if (!IsServer || Store == null || state == null) return;
        var snapshot = Store.Snapshot();
        state.Set("gc_schema", 1); state.Set(ItemsKey, snapshot.Inventory); state.Set(RevisionKey, snapshot.Revision);
    }
    internal static void Register(ZDO zdo)
    {
        if (!IsServer || Store == null) return;
        if (zdo.GetPrefab() == Plugin.ChestHash) Store.Register(zdo.m_uid.ToString());
        if (wardRadii.ContainsKey(zdo.GetPrefab())) wards.Add(zdo.m_uid);
    }
    internal static void Destroyed(ZDO zdo)
    {
        if (!IsServer || Store == null || state == null) return;
        wards.Remove(zdo.m_uid);
        if (zdo.GetPrefab() != Plugin.ChestHash) return;
        if (!Store.Remove(zdo.m_uid.ToString(), out var spill)) return;
        if (spill != null)
        {
            state.Set(SpillKey, spill.Inventory); state.Set("gc_spill_position", zdo.GetPosition());
            state.Set("gc_spill_id", Guid.NewGuid().ToString("N")); state.Set("gc_spill_done", 0L);
        }
        Persist();
        if (spill != null) ResumeSpill();
    }
    private static void ResumeSpill()
    {
        if (state == null) return;
        byte[] bytes = state.GetByteArray(SpillKey);
        if (bytes == null || bytes.Length == 0) return;
        try
        {
            var inventory = InventoryCodec.Read(bytes);
            string id = state.GetString("gc_spill_id");
            long completed = state.GetLong("gc_spill_done");
            var existing = new HashSet<int>(Objects.Where(zdo => zdo.GetString("gc_spill_id") == id && !IsState(zdo)).Select(zdo => zdo.GetInt("gc_spill_index")));
            var items = inventory.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                long bit = 1L << i;
                if ((completed & bit) != 0) continue;
                if (!existing.Contains(i))
                {
                    var position = state.GetVec3("gc_spill_position", Vector3.zero) + Vector3.up * 0.5f + new Vector3((i % 4) * 0.15f, 0, (i / 4) * 0.15f);
                    var drop = ItemDrop.DropItem(items[i], 0, position, Quaternion.identity);
                    var zdo = drop.GetComponent<ZNetView>().GetZDO();
                    zdo.Set("gc_spill_id", id); zdo.Set("gc_spill_index", i);
                }
                completed |= bit; state.Set("gc_spill_done", completed);
            }
            state.Set(SpillKey, Array.Empty<byte>());
        }
        catch (Exception exception) { Plugin.Error(exception); /* Retain pending contents for recovery. */ }
    }

    internal static void Tick()
    {
        // Multiplayer clients never initialize the authority's manager. They must
        // not reset per-world client previews on every frame because it is null.
        if (!IsServer) return;
        if (manager != ZDOMan.instance) { Reset(); return; }
        if (Store == null) return;
        Store.Expire(Time.unscaledTime);
        if (Time.unscaledTime < nextPeerCheck) return;
        nextPeerCheck = Time.unscaledTime + 1;
        var lease = Store.ActiveLease;
        if (lease != null && lease.Peer != ZNet.GetUID() && ZNet.instance.GetPeer(lease.Peer) == null) Store.Disconnect(lease.Peer);
    }
    internal static void Reset() { Store = null; state = null; manager = null; wards.Clear(); wardRadii.Clear(); automationToken = ""; }

    internal static IEnumerator Receive(long sender, ZPackage package)
    {
        try { Handle(sender, package); }
        catch (Exception exception) { Plugin.Error(exception); }
        yield break;
    }
    private static void Handle(long sender, ZPackage package)
    {
        if (!IsServer || Store == null || state == null || package.Size() > InventoryCodec.MaxBytes * 3 + Protocol.HeaderAllowance) return;
        if (sender != ZNet.GetUID() && ZNet.instance.GetPeer(sender) == null) return;
        var operation = (Operation)package.ReadInt();
        var chestId = package.ReadZDOID(); string token = package.ReadString(); long sequence = package.ReadLong();
        var chest = ZDOMan.instance.GetZDO(chestId);
        AccessResult result = AccessResult.InvalidSession;
        string replyToken = token;
        string reason = "The inventory session is no longer valid. Reopen the chest.";
        try
        {
            if (operation == Operation.Close) result = Store.Close(sender, token) ? AccessResult.Accepted : AccessResult.InvalidSession;
            else if (operation == Operation.Commit)
            {
                long revision = package.ReadLong(); int width = package.ReadInt(); int height = package.ReadInt();
                byte[] beforePlayer = package.ReadByteArray(); byte[] newShared = package.ReadByteArray(); byte[] newPlayer = package.ReadByteArray();
                // A committed transfer must still receive its acknowledgement after lease expiry or destruction.
                if (Store.TryReplay(sender, token, sequence, newShared, out var replay)) result = replay;
                else if (chest == null || chest.GetPrefab() != Plugin.ChestHash)
                {
                    result = AccessResult.UnknownChest; reason = "The chest no longer exists on the server.";
                }
                else if (!HasAccess(sender, chest, token == automationToken))
                {
                    result = AccessResult.AccessDenied; reason = "The server denied access: check your distance and ward permissions.";
                }
                else if (!Store.Authorized(sender, token, Time.unscaledTime) || Store.ActiveLease!.Chest != chestId.ToString())
                {
                    result = AccessResult.InvalidSession;
                }
                else if (width < 1 || width > 16 || height < 1 || height > 32)
                {
                    result = AccessResult.InvalidDimensions; reason = $"Unsupported player inventory dimensions: {width} x {height}.";
                }
                else
                {
                    if (revision != Store.Snapshot().Revision) { result = AccessResult.StaleRevision; reason = "The shared inventory changed. Reopen the chest."; }
                    else
                    {
                        var oldShared = ReadInventory(Store.Snapshot().Inventory, "stored guild inventory");
                        var nextShared = ReadInventory(newShared, "guild inventory after transfer");
                        if (InventoryCodec.Conserves(oldShared, beforePlayer, nextShared, newPlayer, width, height))
                        {
                            result = Store.Commit(sender, token, revision, sequence, newShared, Time.unscaledTime);
                            if (result == AccessResult.Accepted) Persist();
                        }
                        else { result = AccessResult.ItemMismatch; reason = "The transfer changes item quantities or types; check inventory mods on client and server."; }
                    }
                }
            }
            else if (chest != null && chest.GetPrefab() == Plugin.ChestHash && HasAccess(sender, chest, operation == Operation.Peek || operation == Operation.OpenAutomation || token == automationToken && token.Length > 0))
            {
                Store.Register(chestId.ToString());
                if (operation == Operation.Peek)
                {
                    result = AccessResult.Accepted;
                }
                else if (operation == Operation.Open || operation == Operation.OpenAutomation)
                {
                    if (state.GetByteArray(SpillKey, Array.Empty<byte>()).Length > 0)
                    {
                        result = AccessResult.RecoveryPending; reason = "Stored items are awaiting recovery from the last destroyed chest. Check the server log.";
                    }
                    else
                    {
                        // Fail closed if an item mod has been removed or the save format changed.
                        ReadInventory(Store.Snapshot().Inventory, "stored guild inventory");
                        result = Store.Open(sender, chestId.ToString(), Time.unscaledTime, out var lease);
                        replyToken = lease?.Token ?? "";
                        if (result == AccessResult.Accepted) automationToken = operation == Operation.OpenAutomation ? replyToken : "";
                    }
                }
                else if (operation == Operation.Heartbeat && Store.ActiveLease?.Chest == chestId.ToString())
                {
                    result = Store.Heartbeat(sender, token, Time.unscaledTime) ? AccessResult.Accepted : AccessResult.InvalidSession;
                }
            }
            else if (chest == null || chest.GetPrefab() != Plugin.ChestHash)
            {
                result = AccessResult.UnknownChest; reason = "The chest no longer exists on the server.";
            }
            else
            {
                result = AccessResult.AccessDenied; reason = "The server denied access: check your distance and ward permissions.";
            }
        }
        catch (Exception exception) { Plugin.Error(exception); result = AccessResult.InvalidInventory; reason = exception.Message; }
        if (result == AccessResult.Busy) reason = "Another player is using the guild inventory.";
        if (result == AccessResult.InvalidSequence) reason = "The transfer request is out of order. Reopen the chest.";
        if (result != AccessResult.Accepted)
            Plugin.LogWarning($"{operation} rejected: {result}; peer={sender}, chest={chestId}, request={sequence}. {reason}");
        var snapshot = Store.Snapshot();
        var reply = Protocol.Header(operation, chestId, replyToken, sequence);
        reply.Write((int)result); reply.Write(snapshot.Revision); reply.Write(snapshot.Inventory);
        reply.Write(result == AccessResult.Accepted ? "" : reason);
        Plugin.ReplyRpc.SendPackage(sender, reply);
    }

    private static Inventory ReadInventory(byte[] bytes, string context, int width = Protocol.Width, int height = Protocol.Height)
    {
        try { return InventoryCodec.Read(bytes, width, height); }
        catch (Exception exception) { throw new InvalidOperationException($"Cannot read {context}: {exception.Message}", exception); }
    }

    private static bool HasAccess(long sender, ZDO chest, bool automation = false)
    {
        long playerId; Vector3 position;
        if (sender == ZNet.GetUID())
        {
            if (!Player.m_localPlayer) return false;
            playerId = Player.m_localPlayer.GetPlayerID(); position = Player.m_localPlayer.transform.position;
        }
        else
        {
            var peer = ZNet.instance.GetPeer(sender); var character = peer == null ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
            if (character == null || character.GetBool(ZDOVars.s_dead)) return false;
            playerId = peer!.m_playerID; position = character.GetPosition();
        }
        if (Vector3.Distance(position, chest.GetPosition()) > (automation ? Plugin.AutomationRange.Value : 6f)) return false;
        bool guarded = false, permitted = false;
        // Dedicated servers need ward data from the world, not the list of rendered ward components.
        foreach (var id in wards)
        {
            var ward = ZDOMan.instance.GetZDO(id);
            if (ward == null || !wardRadii.TryGetValue(ward.GetPrefab(), out float radius) || !ward.GetBool(ZDOVars.s_enabled) || Utils.DistanceXZ(ward.GetPosition(), chest.GetPosition()) >= radius) continue;
            guarded = true;
            if (ward.GetLong(ZDOVars.s_creator) == playerId) permitted = true;
            int count = ward.GetInt(ZDOVars.s_permitted);
            for (int i = 0; i < count; i++) if (ward.GetLong("pu_id" + i) == playerId) permitted = true;
        }
        return !guarded || permitted;
    }
}
