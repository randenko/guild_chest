using System;
using System.Linq;
using UnityEngine;

namespace GuildChest;

// Transfer planned materials, then resume the native crafting/building action.
internal static class MaterialSupply
{
    internal static bool Supply(Piece.Requirement[] requirements, int quality, int multiplier, bool onlyOne, bool building, Action? ready)
        => SupplyCore(new MaterialRequest(requirements, quality, multiplier, onlyOne, building), ready);
    internal static bool SupplyCrafting(Recipe recipe, int quality, int multiplier, Action ready)
        => SupplyCore(new MaterialRequest(recipe.m_resources, quality, multiplier, recipe.m_requireOnlyOneIngredient, false, recipe), ready);
    private static bool SupplyCore(MaterialRequest request, Action? ready)
    {
        if (!CraftyBoxesAdapter.Installed || !StoragePreview.Chest || StoragePreview.Snapshot == null || Time.unscaledTime - StoragePreview.UpdatedAt > Protocol.PreviewLifetime) return false;
        if (Client.Pending || Client.Opening) return true;
        try
        {
            var nearby = CraftyBoxesAdapter.Nearby();
            var planned = MaterialPlanner.Plan(request, Player.m_localPlayer.GetInventory(), StoragePreview.Snapshot, nearby);
            if (planned.Count == 0) return false;
            bool started = GuildChestStorage.TryTransfer(StoragePreview.Chest!, (player, shared) =>
            {
                // Re-check the counts, permissions and rules after acquiring the lease.
                var needs = MaterialPlanner.Plan(request, player, shared, CraftyBoxesAdapter.Nearby());
                foreach (var need in needs)
                {
                    int remaining = need.Amount;
                    foreach (var source in shared.GetAllItems().Where(item => item.m_shared.m_name == need.Name &&
                        (need.Quality < 0 || item.m_quality == need.Quality) && item.m_worldLevel >= Game.m_worldLevel).ToList())
                    {
                        int amount = Math.Min(remaining, source.m_stack);
                        var copy = source.Clone(); copy.m_stack = amount; copy.m_equipped = false;
                        if (!player.AddItem(copy)) throw new InvalidOperationException("Make room in your inventory for guild crafting materials.");
                        shared.RemoveItem(source, amount); remaining -= amount;
                        if (remaining == 0) break;
                    }
                    if (remaining != 0) throw new InvalidOperationException("Guild crafting resources changed; try again.");
                }
            }, (accepted, reason) =>
            {
                if (!accepted) Plugin.Message($"Guild material transfer failed: {reason}");
                else ready?.Invoke();
            });
            if (!started) Plugin.Message("The guild inventory is busy. Try again.");
            return true;
        }
        catch (InvalidOperationException exception)
        {
            // GetAmount dereferences the chosen one-ingredient item. Aggregate
            // preview counts can enable the button without a usable quality.
            if (request.OnlyOne && request.Recipe) { Plugin.Message(exception.Message); return true; }
            return false;
        }
    }
}
