namespace GuildChest;

internal enum StorageScope { Ui, Preview, Consumption }

internal static class StorageScopes
{
    internal static int UiDepth, PreviewDepth, ConsumptionDepth;

    // Mark Harmony state before invalidation: its finalizer must balance the
    // counter even when another mod's cache notification throws.
    internal static void Enter(StorageScope scope, ref bool entered)
    {
        entered = true;
        switch (scope)
        {
            case StorageScope.Ui: UiDepth++; break;
            case StorageScope.Preview:
                if (PreviewDepth++ == 0) CraftyBoxesAdapter.InvalidateCounts();
                break;
            case StorageScope.Consumption:
                ConsumptionDepth++; CraftyBoxesAdapter.InvalidateCounts(); break;
        }
    }
    internal static void Exit(StorageScope scope, bool entered)
    {
        if (!entered) return;
        switch (scope)
        {
            case StorageScope.Ui: UiDepth--; break;
            case StorageScope.Preview:
                if (--PreviewDepth == 0) CraftyBoxesAdapter.InvalidateCounts();
                break;
            case StorageScope.Consumption:
                ConsumptionDepth--; CraftyBoxesAdapter.InvalidateCounts(); break;
        }
    }
}
