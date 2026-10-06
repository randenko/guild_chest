using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;

namespace GuildChest;

internal static class InventoryCodec
{
    internal const int MaxBytes = 1024 * 1024;
    internal static byte[] Save(Inventory inventory)
    {
        var package = new ZPackage(); inventory.Save(package); return package.GetArray();
    }
    internal static Inventory Empty(int width = 8, int height = 4) => new("Guild Chest", null, width, height);

    internal static Inventory Read(byte[] bytes, int width = 8, int height = 4)
    {
        var result = Empty(width, height);
        foreach (var entry in ReadEntries(bytes, width, height))
        {
            var item = entry.Item;
            var prefab = ObjectDB.instance.GetItemPrefab(entry.Hash);
            if (!prefab || !prefab.GetComponent<ItemDrop>())
                throw new InvalidOperationException($"Missing shared-storage item prefab {entry.Hash}; install the item's mod on both client and server.");
            item.m_dropPrefab = prefab;
            item.m_shared = prefab.GetComponent<ItemDrop>().m_itemData.m_shared;
            if (item.m_stack > item.m_shared.m_maxStackSize)
                throw new InvalidOperationException($"Invalid item {prefab.name}: stack {item.m_stack} exceeds server maximum {item.m_shared.m_maxStackSize}.");
            result.GetAllItems().Add(item);
        }
        Changed(result); return result;
    }

    private sealed class Entry
    {
        internal int Hash = 0;
        internal ItemDrop.ItemData Item = null!;
        internal string UnchangedKey = "";
        internal string QuantityKey => $"{Hash}:{Item.m_quality}:{Item.m_variant}:{Item.m_worldLevel}";
    }

    // ItemData.Load reads the native record without needing its prefab. Player snapshots
    // can contain client-only items, which must remain unchanged outside shared storage.
    private static List<Entry> ReadEntries(byte[] bytes, int width, int height)
    {
        if (bytes.Length > MaxBytes) throw new InvalidOperationException("Inventory exceeds the supported package size.");
        var package = new ZPackage(bytes);
        int version = package.ReadInt();
        if (version != 109) throw new InvalidOperationException($"Unsupported inventory format {version}; saved data has been retained.");
        int count = package.ReadUShort();
        if (count > width * height) throw new InvalidOperationException("Too many inventory entries.");
        var result = new List<Entry>();
        var occupied = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int start = package.GetPos();
            var (hash, item) = ItemDrop.ItemData.Load(package, (global::Version.Item)version);
            if (item.m_stack <= 0 || item.m_quality < 1 ||
                item.m_gridPos.x < 0 || item.m_gridPos.x >= width || item.m_gridPos.y < 0 || item.m_gridPos.y >= height ||
                !occupied.Add(item.m_gridPos.y * width + item.m_gridPos.x))
                throw new InvalidOperationException($"Invalid item prefab {hash}: stack {item.m_stack}, quality {item.m_quality}, slot ({item.m_gridPos.x}, {item.m_gridPos.y}) in {width} x {height} inventory, or duplicate slot.");
            var encoded = new byte[package.GetPos() - start];
            Array.Copy(bytes, start, encoded, 0, encoded.Length);
            // Format 109 starts with durability (4 bytes), then grid X/Y. Allow local
            // rearrangement while preserving every other byte of an unresolved item.
            encoded[4] = 0; encoded[5] = 0;
            result.Add(new Entry { Hash = hash, Item = item, UnchangedKey = Convert.ToBase64String(encoded) });
        }
        if (package.GetPos() != package.Size()) throw new InvalidOperationException("Unexpected trailing inventory data.");
        return result;
    }

    internal static Inventory Clone(Inventory source)
    {
        var clone = new Inventory(source.GetName(), source.GetBkg(), source.GetWidth(), source.GetHeight());
        clone.GetAllItems().AddRange(source.GetAllItems().Select(item => item.Clone())); Changed(clone); return clone;
    }
    internal static void Replace(Inventory target, Inventory source)
    {
        target.GetAllItems().Clear(); target.GetAllItems().AddRange(source.GetAllItems().Select(item => item.Clone())); Changed(target);
    }
    internal static void Changed(Inventory inventory) => AccessTools.Method(typeof(Inventory), "Changed").Invoke(inventory, new object[] { false, false });

    // Stacking follows vanilla's rules, including the destination stack's metadata.
    internal static bool Conserves(Inventory oldShared, byte[] oldPlayer, Inventory newShared, byte[] newPlayer, int width, int height)
    {
        List<Entry> PlayerEntries(byte[] bytes, string context)
        {
            try { return ReadEntries(bytes, width, height); }
            catch (Exception exception) { throw new InvalidOperationException($"Cannot read {context}: {exception.Message}", exception); }
        }
        var beforePlayer = PlayerEntries(oldPlayer, "player inventory before transfer");
        var afterPlayer = PlayerEntries(newPlayer, "player inventory after transfer");
        IEnumerable<Entry> SharedEntries(Inventory inventory) => inventory.GetAllItems()
            .Select(item => new Entry { Hash = item.m_dropPrefab.name.GetStableHashCode(), Item = item });
        Dictionary<string, long> Count(IEnumerable<Entry> items) => items.GroupBy(entry => entry.QuantityKey)
            .ToDictionary(group => group.Key, group => group.Sum(entry => (long)entry.Item.m_stack));
        var before = Count(SharedEntries(oldShared).Concat(beforePlayer));
        var after = Count(SharedEntries(newShared).Concat(afterPlayer));
        if (before.Count != after.Count || !before.All(pair => after.TryGetValue(pair.Key, out long amount) && amount == pair.Value)) return false;

        bool MissingPrefab(Entry entry)
        {
            var prefab = ObjectDB.instance.GetItemPrefab(entry.Hash);
            return !prefab || !prefab.GetComponent<ItemDrop>();
        }
        var unknownBefore = beforePlayer.Where(MissingPrefab).Select(entry => entry.UnchangedKey).OrderBy(key => key, StringComparer.Ordinal);
        var unknownAfter = afterPlayer.Where(MissingPrefab).Select(entry => entry.UnchangedKey).OrderBy(key => key, StringComparer.Ordinal);
        return unknownBefore.SequenceEqual(unknownAfter);
    }
}
