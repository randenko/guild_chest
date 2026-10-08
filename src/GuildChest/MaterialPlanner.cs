using System;
using System.Collections.Generic;
using System.Linq;

namespace GuildChest;

internal sealed class MaterialRequest
{
    internal readonly Piece.Requirement[] Requirements;
    internal readonly int Quality, Multiplier;
    internal readonly bool OnlyOne, Building;
    internal readonly Recipe? Recipe;
    internal MaterialRequest(Piece.Requirement[] requirements, int quality, int multiplier, bool onlyOne, bool building, Recipe? recipe = null)
    {
        Requirements = requirements; Quality = quality; Multiplier = multiplier;
        OnlyOne = onlyOne; Building = building; Recipe = recipe;
    }
}

// Compute deficits only; this code never changes either inventory.
internal static class MaterialPlanner
{
    private static int Takeable(int count, bool leaveOne) => Math.Max(0, count - (leaveOne ? 1 : 0));
    internal sealed class MaterialNeed
    {
        internal readonly string Name;
        internal readonly int Amount, Quality;
        internal MaterialNeed(string name, int amount, int quality = -1) { Name = name; Amount = amount; Quality = quality; }
    }
    private static List<MaterialNeed> OneIngredientNeeds(MaterialRequest request, Inventory player, Inventory shared, object? guild)
    {
        var recipe = request.Recipe!;
        // Let vanilla and CraftyBoxes choose any ingredient they can already use
        // without guild storage, including their ordinary-container preference.
        if (Player.m_localPlayer.GetFirstRequiredItem(player, recipe, request.Quality, out _, out _, request.Multiplier) != null) return new();
        var station = Player.m_localPlayer.GetCurrentCraftingStation();
        foreach (var requirement in recipe.m_resources)
        {
            if (!requirement.m_resItem || requirement.m_upgraderResource != (station && station.m_upgrader)) continue;
            int amount = requirement.GetAmount(request.Quality) * request.Multiplier;
            if (amount <= 0 || guild == null || !CraftyBoxesAdapter.Pullable(guild, requirement.m_resItem.gameObject.name)) continue;
            string name = requirement.m_resItem.m_itemData.m_shared.m_name;
            // Leave One reserves one item of this ingredient in the container,
            // just as upstream does; it does not reserve one of every quality.
            int takeable = Takeable(shared.CountItems(name), CraftyBoxesAdapter.LeaveOne);
            for (int itemQuality = 0; itemQuality <= requirement.m_resItem.m_itemData.m_shared.m_maxQuality; itemQuality++)
            {
                int needed = Math.Max(0, amount - player.CountItems(name, itemQuality));
                if (needed > 0 && needed <= Math.Min(takeable, shared.CountItems(name, itemQuality)))
                    return new() { new MaterialNeed(name, needed, itemQuality) };
            }
        }
        throw new InvalidOperationException("Not enough allowed ingredients of one quality remain in guild storage.");
    }
    internal static List<MaterialNeed> Plan(MaterialRequest request, Inventory player, Inventory shared, List<object> nearby)
    {
        var guild = nearby.FirstOrDefault(wrapper => ContainerBindings.ContainerFor(wrapper) == StoragePreview.Chest);
        if (request.OnlyOne)
        {
            if (!request.Recipe) throw new InvalidOperationException("A recipe is required to select an ingredient quality.");
            return OneIngredientNeeds(request, player, shared, guild);
        }
        var result = new List<MaterialNeed>();
        var station = Player.m_localPlayer.GetCurrentCraftingStation();
        bool leaveOne = CraftyBoxesAdapter.LeaveOne;
        foreach (var requirement in request.Requirements)
        {
            if (!requirement.m_resItem || !request.Building && requirement.m_upgraderResource != (station && station.m_upgrader)) continue;
            int amount = requirement.GetAmount(request.Quality) * request.Multiplier;
            if (amount <= 0) continue;
            string name = requirement.m_resItem.m_itemData.m_shared.m_name;
            string prefab = requirement.m_resItem.gameObject.name;
            int available = player.CountItems(name);
            foreach (var container in nearby)
                if (!Plugin.IsGuild(ContainerBindings.ContainerFor(container)) && CraftyBoxesAdapter.Pullable(container, prefab))
                    available += Takeable(ContainerBindings.Count(container, name), leaveOne);
            int needed = Math.Max(0, amount - available);
            int inGuild = guild != null && CraftyBoxesAdapter.Pullable(guild, prefab) ? Takeable(shared.CountItems(name), leaveOne) : 0;
            if (needed > inGuild) throw new InvalidOperationException("Not enough allowed resources remain in nearby storage.");
            if (needed > 0) result.Add(new MaterialNeed(name, needed));
        }
        return result;
    }
}
