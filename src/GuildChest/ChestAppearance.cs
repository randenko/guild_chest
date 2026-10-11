using System.Collections.Generic;
using System.Linq;
using Jotunn.Managers;
using UnityEngine;

namespace GuildChest;

internal static class ChestAppearance
{
    internal const string DecorationName = "GuildChestCoins";

    internal static void Apply(GameObject prefab)
    {
        // Tint the chest before attaching coins so they retain their vanilla gold finish.
        foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            // Renderer.materials creates instances, leaving vanilla chest materials untouched.
            foreach (var material in renderer.materials)
                if (material.HasProperty("_Color")) material.color *= new Color(1f, 0.78f, 0.3f, 1f);
        }

        var coins = PrefabManager.Instance.GetPrefab("Coins");
        var coin = coins ? coins.GetComponentsInChildren<MeshFilter>(true)
            .FirstOrDefault(filter => filter.sharedMesh && filter.sharedMesh.name == "coin") : null;
        var coinRenderer = coin ? coin.GetComponent<MeshRenderer>() : null;
        var body = prefab.GetComponentsInChildren<MeshFilter>(true)
            .FirstOrDefault(filter => filter.sharedMesh && filter.sharedMesh.name == "ironchest");
        var container = prefab.GetComponent<Container>();
        var closedLid = container.m_closed ? container.m_closed.GetComponent<MeshFilter>() : null;
        var openLid = container.m_open ? container.m_open.GetComponent<MeshFilter>() : null;
        if (!coin || !coinRenderer || !coin.sharedMesh.isReadable || !body || !closedLid || !openLid)
        {
            Plugin.LogWarning("Guild chest coin decorations unavailable: expected vanilla coin or chest meshes are missing.");
            return;
        }

        var bodyCoins = new List<CombineInstance>();
        var bodyBounds = body.sharedMesh.bounds;
        foreach (int face in new[] { -1, 1 })
        {
            var rotation = Quaternion.Euler(90f * face, 0f, 0f);
            float surface = face < 0 ? bodyBounds.min.z : bodyBounds.max.z;
            AddCoin(bodyCoins, coin.sharedMesh,
                new Vector3(bodyBounds.center.x, bodyBounds.center.y, surface), rotation, 0.46f);
            // Small mounted coins frame the central treasury seal on either face.
            foreach (float x in new[] { -0.55f, 0.55f })
                foreach (float y in new[] { 0.38f, 0.72f })
                    AddCoin(bodyCoins, coin.sharedMesh, new Vector3(x, y, surface), rotation, 0.13f);
        }
        AddDecoration(body.transform, Combine("GuildChestCoinSeals", bodyCoins), coinRenderer);

        var lidCoins = new List<CombineInstance>();
        var lidBounds = closedLid.sharedMesh.bounds;
        for (int i = -2; i <= 2; i++)
            AddCoin(lidCoins, coin.sharedMesh,
                new Vector3(lidBounds.center.x + i * 0.23f, lidBounds.max.y, lidBounds.center.z),
                Quaternion.Euler(0f, i * 18f, 0f), i == 0 ? 0.23f : 0.17f);
        var lidMesh = Combine("GuildChestCoinLid", lidCoins);
        // Vanilla switches between two lid models instead of animating one lid.
        // Sharing the decoration mesh under both keeps the coins attached in either state.
        AddDecoration(closedLid.transform, lidMesh, coinRenderer);
        AddDecoration(openLid.transform, lidMesh, coinRenderer);
    }

    private static void AddCoin(List<CombineInstance> instances, Mesh coin, Vector3 surface,
        Quaternion rotation, float diameter)
    {
        var bounds = coin.bounds;
        float scale = diameter / Mathf.Max(bounds.size.x, bounds.size.z);
        // The coin's thin axis is Y. Place its underside just above the mounting surface.
        var position = surface + rotation * Vector3.up * (bounds.extents.y * scale + 0.006f);
        instances.Add(new CombineInstance {
            mesh = coin,
            transform = Matrix4x4.TRS(position - rotation * (bounds.center * scale), rotation, Vector3.one * scale)
        });
    }

    private static Mesh Combine(string name, List<CombineInstance> coins)
    {
        var mesh = new Mesh { name = name };
        mesh.CombineMeshes(coins.ToArray());
        return mesh;
    }

    private static void AddDecoration(Transform parent, Mesh mesh, MeshRenderer source)
    {
        // Copy only geometry and materials: no pickup, physics, particles, or network behaviour.
        var decoration = new GameObject(DecorationName) { layer = parent.gameObject.layer };
        decoration.transform.SetParent(parent, false);
        decoration.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = decoration.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = source.sharedMaterials;
        renderer.shadowCastingMode = source.shadowCastingMode;
        renderer.receiveShadows = source.receiveShadows;
    }
}
