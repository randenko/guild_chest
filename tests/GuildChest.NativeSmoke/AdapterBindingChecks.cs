using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace GuildChest.NativeSmoke;

public sealed partial class NativeSmokeHarness
{
    private static int BindingTarget() => 7;
    private static bool BindingPrefix(ref int __result) { __result = 9; return false; }

    private void VerifyAdapterBindings(Player player, Container chest, Container second)
    {
        var assembly = typeof(GuildChest.Plugin).Assembly;
        var access = assembly.GetType("GuildChest.InventoryAccess")!;
        var scopeType = access.GetNestedType("StagingScope", BindingFlags.NonPublic)!;
        var scopedChest = access.GetProperty("ScopedChest", Flags)!;
        var inventoryField = AccessTools.Field(typeof(Humanoid), "m_inventory");
        var chestField = AccessTools.Field(typeof(Container), "m_inventory");
        var originalPlayer = player.GetInventory();
        var originalChest = chestField.GetValue(chest);
        var originalSecond = chestField.GetValue(second);
        IDisposable Stage(Container container, Inventory personal, Inventory shared) => (IDisposable)Activator.CreateInstance(scopeType,
            BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { player, container, personal, shared }, null);
        var outerPersonal = new Inventory("outer player", null, 8, 4);
        var outerShared = new Inventory("outer shared", null, 8, 4);
        using (Stage(chest, outerPersonal, outerShared))
        {
            Check(ReferenceEquals(player.GetInventory(), outerPersonal) && ReferenceEquals(chest.GetInventory(), outerShared), "Staging exposes disposable inventories");
            try
            {
                using var inner = Stage(second, new Inventory("inner player", null, 8, 4), new Inventory("inner shared", null, 8, 4));
                Check(ReferenceEquals(scopedChest.GetValue(null), second), "Nested staging selects inner access point");
                throw new InvalidOperationException("Fixture: staged rule failure");
            }
            catch (InvalidOperationException exception) when (exception.Message == "Fixture: staged rule failure") { }
            Check(ReferenceEquals(player.GetInventory(), outerPersonal) && ReferenceEquals(chest.GetInventory(), outerShared) &&
                ReferenceEquals(scopedChest.GetValue(null), chest) && ReferenceEquals(chestField.GetValue(second), originalSecond), "Throwing nested staging restores enclosing scope");
        }
        Check(ReferenceEquals(inventoryField.GetValue(player), originalPlayer) && ReferenceEquals(chestField.GetValue(chest), originalChest) &&
            scopedChest.GetValue(null) == null, "Staging restores live inventories and clears owner override");

        var reflection = assembly.GetType("GuildChest.StorageBindings")!;
        var hook = AccessTools.Method(typeof(NativeSmokeHarness), nameof(BindingTarget));
        var harmony = new Harmony("com.randenko.guildchest.smoke.bindings");
        try
        {
            bool rejected = false;
            try
            {
                AccessTools.Method(reflection, "Patch").Invoke(null, new object[] { harmony, typeof(NativeSmokeHarness),
                    new[] { (hook, nameof(BindingPrefix)), (hook, "MissingAdapterPrefix") } });
            }
            catch (TargetInvocationException exception) when (exception.InnerException is MissingMethodException) { rejected = true; }
            Check(rejected && Harmony.GetPatchInfo(hook)?.Prefixes.All(prefix => prefix.owner != harmony.Id) != false,
                "Missing required binding installs no partial hooks");
            AccessTools.Method(reflection, "Patch").Invoke(null, new object[] { harmony, typeof(NativeSmokeHarness), new[] { (hook, nameof(BindingPrefix)) } });
            Check((int)hook.Invoke(null, null) == 9, "Valid bindings install their hooks");
        }
        finally { harmony.UnpatchSelf(); }
        Check((int)hook.Invoke(null, null) == 7, "Binding fixture removes its hooks");
        Logger.LogInfo("NATIVE_SMOKE_ADAPTER_BINDINGS_PASSED");
    }
}
