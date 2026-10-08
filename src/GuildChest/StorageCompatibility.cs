using System;
using BepInEx.Bootstrap;
using HarmonyLib;

namespace GuildChest;

// World lifecycle and optional adapter installation only. Keep each mod's
// reflection, rules and patches with that adapter.
internal static class StorageCompatibility
{
    internal static void Install(Harmony harmony)
    {
        Install(AutoStoreAdapter.PluginId, type => AutoStoreAdapter.Install(harmony, type));
        Install(CraftyBoxesAdapter.PluginId, type => CraftyBoxesAdapter.Install(harmony, type));
    }
    private static void Install(string id, Action<Type> install)
    {
        if (!Chainloader.PluginInfos.TryGetValue(id, out var mod)) return;
        try
        {
            install(mod.Instance.GetType());
            Plugin.LogInfo($"{mod.Metadata.Name} adapter installed ({mod.Metadata.Version}).");
        }
        catch (Exception exception)
        {
            Plugin.LogWarning($"{mod.Metadata.Name} adapter disabled ({mod.Metadata.Version}): {exception.Message} Unadapted guild writes remain blocked.");
        }
    }
    internal static void Tick() { AutoStoreBatch.Tick(); StoragePreview.Tick(); }
    internal static void Reset() { StoragePreview.Reset(); AutoStoreBatch.Reset(); }
}
