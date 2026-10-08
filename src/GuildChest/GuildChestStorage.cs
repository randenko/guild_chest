using System;

namespace GuildChest;

/// <summary>Client-side storage integration. Call on Unity's main thread.</summary>
public static class GuildChestStorage
{
    public static bool IsGuildChest(Container container) => Plugin.IsGuild(container);

    /// <summary>
    /// Acquires the server lease and runs a transfer against disposable player/shared
    /// inventories. Move items between these inventories; do not retain their references
    /// or perform gameplay side effects. Completion follows the server acknowledgement.
    /// False means the request could not start; neither inventory has changed.
    /// </summary>
    public static bool TryTransfer(Container chest, Action<Inventory, Inventory> transfer, Action<bool, string>? completed = null)
    {
        if (!Plugin.IsGuild(chest) || transfer == null) return false;
        return Client.Transfer(chest, transfer, completed);
    }
}

