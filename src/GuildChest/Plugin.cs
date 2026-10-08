using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Jotunn.Utils;
using UnityEngine;

namespace GuildChest;

[BepInPlugin(Id, "Guild Chest", ModVersion)]
[BepInDependency(Jotunn.Main.ModGuid)]
[BepInDependency(AutoStoreAdapter.PluginId, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(CraftyBoxesAdapter.PluginId, BepInDependency.DependencyFlags.SoftDependency)]
[NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Patch)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Id = "com.randenko.guildchest";
    public const string ModVersion = BuildVersion.Value;
    public const string ChestName = "GuildChest";
    internal const string StateName = "GuildChestWorldState";
    internal static readonly int ChestHash = ChestName.GetStableHashCode();
    internal static readonly int StateHash = StateName.GetStableHashCode();
    internal static Plugin Instance = null!;
    internal static CustomRPC RequestRpc = null!;
    internal static CustomRPC ReplyRpc = null!;
    private Harmony? harmony;
    internal static ConfigEntry<float> AutomationRange = null!;

    private void Awake()
    {
        Instance = this;
        AutomationRange = Config.Bind("Compatibility", "Automation range", 20f,
            new ConfigDescription("Maximum player distance for storage-mod transfers and resource previews. The server enforces its value.", new AcceptableValueRange<float>(1f, 100f)));
        RequestRpc = NetworkManager.Instance.AddRPC(Protocol.RequestRpc, Host.Receive, IgnoreRequest);
        // A host player's replies are delivered on the server too, so both roles must handle responses.
        ReplyRpc = NetworkManager.Instance.AddRPC(Protocol.ReplyRpc, Client.Receive, Client.Receive);
        PrefabManager.OnVanillaPrefabsAvailable += RegisterPrefabs;
        harmony = new Harmony(Id);
        harmony.PatchAll(typeof(Plugin).Assembly);
        StorageCompatibility.Install(harmony);
        Logger.LogInfo("Guild Chest loaded. Shared inventory: 32 slots, one user per world.");
    }

    private void RegisterPrefabs()
    {
        var prefab = PrefabManager.Instance.CreateClonedPrefab(ChestName, "piece_chest");
        if (!prefab) throw new InvalidOperationException("Reinforced chest prefab is unavailable.");
        var container = prefab.GetComponent<Container>();
        container.m_name = "Guild Chest";
        container.m_width = Protocol.Width; container.m_height = Protocol.Height;
        container.m_privacy = Container.PrivacySetting.Public;
        container.m_checkGuardStone = true;
        container.m_autoDestroyEmpty = false;
        container.m_defaultItems = new DropTable();
        foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
        {
            // Renderer.materials creates instances, leaving vanilla chest materials untouched.
            foreach (var material in renderer.materials)
            {
                if (material.HasProperty("_Color")) material.color *= new Color(0.55f, 0.8f, 1f, 1f);
            }
        }
        var config = new PieceConfig {
            Name = "Guild Chest", Description = "One inventory shared by every guild chest in this world.",
            PieceTable = "Hammer", CraftingStation = "piece_workbench"
        };
        config.AddRequirement("FineWood", 20); config.AddRequirement("Iron", 10); config.AddRequirement("SurtlingCore", 2);
        config.AddRequirement("Coins", 250);
        PieceManager.Instance.AddPiece(new CustomPiece(prefab, false, config));

        var statePrefab = PrefabManager.Instance.CreateEmptyPrefab(StateName);
        foreach (var renderer in statePrefab.GetComponents<Renderer>()) DestroyImmediate(renderer);
        foreach (var collider in statePrefab.GetComponents<Collider>()) DestroyImmediate(collider);
        var view = statePrefab.GetComponent<ZNetView>();
        view.m_persistent = true; view.m_type = ZDO.ObjectType.Solid;
        PrefabManager.Instance.AddPrefab(statePrefab);
        PrefabManager.OnVanillaPrefabsAvailable -= RegisterPrefabs;
    }

    private void Update() { Host.Tick(); Client.Tick(); StorageCompatibility.Tick(); }
    private static IEnumerator IgnoreRequest(long sender, ZPackage package) { yield break; }
    private void OnDestroy()
    {
        PrefabManager.OnVanillaPrefabsAvailable -= RegisterPrefabs;
        harmony?.UnpatchSelf();
    }
    internal static void Error(Exception exception) => Instance.Logger.LogError(exception);
    internal static void LogInfo(string message) => Instance.Logger.LogInfo(message);
    internal static void LogWarning(string message) => Instance.Logger.LogWarning(message);
    internal static bool IsGuild(Container? container) => container &&
        (container.GetComponent<ZNetView>()?.GetZDO()?.GetPrefab() == ChestHash || Utils.GetPrefabName(container.gameObject) == ChestName);
    internal static void Message(string text)
    {
        if (Player.m_localPlayer) Player.m_localPlayer.Message(MessageHud.MessageType.Center, text);
    }
    internal static long ServerId => ZNet.instance.IsServer() ? ZNet.GetUID() : ZNet.instance.GetServerPeer().m_uid;
}
