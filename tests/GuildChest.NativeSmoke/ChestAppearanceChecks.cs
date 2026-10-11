using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace GuildChest.NativeSmoke;

public sealed partial class NativeSmokeHarness
{
    private void CheckChestAppearance(GameObject prefab, GameObject instance)
    {
        const string decorationName = "GuildChestCoins";
        var vanilla = ZNetScene.instance.GetPrefab("piece_chest");
        var coins = ObjectDB.instance.GetItemPrefab("Coins");
        var source = coins.GetComponentsInChildren<MeshFilter>(true)
            .Single(filter => filter.sharedMesh && filter.sharedMesh.name == "coin");
        var sourceMaterials = source.GetComponent<MeshRenderer>().sharedMaterials;
        var decorations = prefab.GetComponentsInChildren<Transform>(true)
            .Where(transform => transform.name == decorationName).ToArray();
        Check(decorations.Length == 3, "Coin seals and both lid decorations registered");
        foreach (var decoration in decorations)
        {
            Check(decoration.GetComponents<Component>().All(component =>
                component is Transform || component is MeshFilter || component is MeshRenderer),
                "Decorations contain only geometry: no pickups, colliders or network components");
            var renderer = decoration.GetComponent<MeshRenderer>();
            Check(renderer.sharedMaterials.SequenceEqual(sourceMaterials), "Coins retain vanilla gold materials");
            Check(decoration.GetComponent<MeshFilter>().sharedMesh.vertexCount > source.sharedMesh.vertexCount,
                "Mounted coin geometry combined");
        }
        Check(prefab.GetComponentsInChildren<Collider>(true).Length == vanilla.GetComponentsInChildren<Collider>(true).Length &&
            prefab.GetComponentsInChildren<ZNetView>(true).Length == vanilla.GetComponentsInChildren<ZNetView>(true).Length &&
            prefab.GetComponentsInChildren<ItemDrop>(true).Length == 0, "Appearance preserves chest interaction and network hierarchy");
        Check(!vanilla.GetComponentsInChildren<Transform>(true).Any(transform => transform.name == decorationName) &&
            !coins.GetComponentsInChildren<Transform>(true).Any(transform => transform.name == decorationName),
            "Vanilla chest and coin prefabs remain undecorated");

        var container = instance.GetComponent<Container>();
        var inUse = AccessTools.Field(typeof(Container), "m_inUse");
        var update = AccessTools.Method(typeof(Container), "UpdateUseVisual");
        var originalInUse = inUse.GetValue(container);
        try
        {
            var closed = container.m_closed.transform.Find(decorationName) ?? throw new InvalidOperationException("Closed-lid coins missing");
            var open = container.m_open.transform.Find(decorationName) ?? throw new InvalidOperationException("Open-lid coins missing");
            Check(closed.GetComponent<MeshFilter>().sharedMesh == open.GetComponent<MeshFilter>().sharedMesh,
                "Open and closed lids share the same coin design");
            inUse.SetValue(container, false);
            update.Invoke(container, null);
            Check(closed.gameObject.activeInHierarchy && !open.gameObject.activeInHierarchy,
                "Closed chest displays only closed-lid coins");
            inUse.SetValue(container, true);
            update.Invoke(container, null);
            Check(open.gameObject.activeInHierarchy && !closed.gameObject.activeInHierarchy,
                "Opening chest switches decorations with the lid");
        }
        finally
        {
            inUse.SetValue(container, originalInUse);
            update.Invoke(container, null);
        }
        Logger.LogInfo("NATIVE_SMOKE_CHEST_APPEARANCE_PASSED");
    }
}
