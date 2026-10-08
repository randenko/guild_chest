using System;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace GuildChest;

internal static class InventoryAccess
{
    private static readonly ConditionalWeakTable<Inventory, object> protectedInventories = new();
    private static readonly object marker = new();
    internal static readonly AccessTools.FieldRef<Container, Inventory> ContainerInventory = AccessTools.FieldRefAccess<Container, Inventory>("m_inventory");
    private static readonly AccessTools.FieldRef<Humanoid, Inventory> PlayerInventory = AccessTools.FieldRefAccess<Humanoid, Inventory>("m_inventory");
    internal static Container? ScopedChest { get; private set; }
    private static Inventory? scopedShared;

    internal static void Protect(Inventory inventory) => protectedInventories.GetValue(inventory, _ => marker);
    internal static bool Blocked(Inventory inventory) => protectedInventories.TryGetValue(inventory, out _) ||
        (Client.Pending && !Client.Applying && Player.m_localPlayer && inventory == Player.m_localPlayer.GetInventory());
    private static Inventory Unavailable()
    {
        var inventory = new Inventory("Guild Chest (transaction required)", null, 0, 0);
        Protect(inventory); return inventory;
    }
    internal static void Track(Container container)
    {
        // Never leave a writable, disconnected vanilla inventory available to automation.
        if (Plugin.IsGuild(container)) ContainerInventory(container) = Unavailable();
    }
    internal static Inventory InventoryFor(Container container)
    {
        if (container == ScopedChest) return scopedShared!;
        if (StorageScopes.UiDepth > 0 && Client.Owns(container)) return Client.View!;
        var stored = ContainerInventory(container);
        return stored == Client.View || stored.GetWidth() != 0 ? Unavailable() : stored;
    }

    // Upstream AutoStore must see disposable inventories while applying its own
    // rules. Restore both inventories and any enclosing scope even on failure.
    internal sealed class StagingScope : IDisposable
    {
        private readonly Player player;
        private readonly Container chest;
        private readonly Inventory originalPlayer, originalChest;
        private readonly Container? previousChest;
        private readonly Inventory? previousShared;
        private bool disposed;
        internal StagingScope(Player player, Container chest, Inventory personal, Inventory shared)
        {
            this.player = player; this.chest = chest;
            originalPlayer = PlayerInventory(player); originalChest = ContainerInventory(chest);
            previousChest = ScopedChest; previousShared = scopedShared;
            PlayerInventory(player) = personal; ContainerInventory(chest) = shared;
            ScopedChest = chest; scopedShared = shared;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            PlayerInventory(player) = originalPlayer; ContainerInventory(chest) = originalChest;
            ScopedChest = previousChest; scopedShared = previousShared;
        }
    }
}
