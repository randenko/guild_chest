using HarmonyLib;
using UnityEngine;

namespace GuildChest;

// Replay the accepted click through Player.UpdatePlacement, so vanilla and other
// patches still handle resources, stamina, tool durability, skills and build effects.
internal static class BuildingContinuation
{
    private sealed class Intent
    {
        internal Player Player = null!;
        internal Piece Piece = null!;
        internal ItemDrop.ItemData Tool = null!;
        internal Vector3 Position, PlayerPosition;
        internal Quaternion Rotation;
        internal float Started;
        internal bool Ready;
    }
    private static Intent? pending;
    private static bool replaying;
    private static readonly System.Reflection.MethodInfo updateGhost = AccessTools.Method(typeof(Player), "UpdatePlacementGhost", new[] { typeof(bool) });
    private static GameObject Ghost(Player player) => AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost")(player);
    internal static void Reset() { pending = null; replaying = false; }

    private static bool MatchesContext(Intent intent) => intent.Player && intent.Player == Player.m_localPlayer &&
        !intent.Player.IsDead() && !intent.Player.IsTeleporting() && intent.Player.InPlaceMode() &&
        intent.Player.GetSelectedPiece() == intent.Piece && intent.Player.RightItem == intent.Tool &&
        Vector3.Distance(intent.Player.transform.position, intent.PlayerPosition) <= 0.5f &&
        Time.unscaledTime - intent.Started <= Protocol.OpenTimeout;
    private static bool Matches(Intent intent)
    {
        if (!MatchesContext(intent)) return false;
        var ghost = Ghost(intent.Player);
        return ghost && ghost.activeInHierarchy && Vector3.Distance(ghost.transform.position, intent.Position) <= 0.1f &&
            Quaternion.Angle(ghost.transform.rotation, intent.Rotation) <= 1f;
    }

    internal static bool TryPlace(Player player, Piece piece)
    {
        if (replaying)
        {
            bool valid = pending != null && pending.Piece == piece && Matches(pending);
            Reset(); return valid;
        }
        if (pending != null || Client.Pending || Client.Opening) return false;
        if (player.NoCostCheat() || ZoneSystem.instance.GetGlobalKey(piece.FreeBuildKey())) return true;
        var ghost = Ghost(player);
        var tool = player.RightItem;
        if (!player.InPlaceMode() || player.GetSelectedPiece() != piece || !ghost || tool == null) return true;
        updateGhost.Invoke(player, new object[] { true });
        if (player.GetPlacementStatus() != Player.PlacementStatus.Valid || !ghost.activeInHierarchy) return true;
        var intent = new Intent {
            Player = player, Piece = piece, Tool = tool,
            Position = ghost.transform.position, Rotation = ghost.transform.rotation,
            PlayerPosition = player.transform.position, Started = Time.unscaledTime
        };
        pending = intent;
        if (MaterialSupply.Supply(piece.m_resources, 0, 1, false, true, () =>
        {
            if (pending == intent) intent.Ready = true;
        })) return false;
        pending = null; return true;
    }

    internal static void BeforeUpdate(Player player, bool takeInput)
    {
        if (player != Player.m_localPlayer || pending == null) return;
        if (!takeInput || !MatchesContext(pending) || Hud.IsPieceSelectionVisible()) { Reset(); return; }
        // Inventory changes can recreate the ghost at a temporary default position.
        // Refresh the mouse/controller target before comparing the logical pose.
        updateGhost.Invoke(player, new object[] { false });
        if (!Matches(pending) || player.GetPlacementStatus() != Player.PlacementStatus.Valid) { Reset(); return; }
        if (!pending.Ready)
        {
            if (!Client.Pending && !Client.Opening) Reset();
            return;
        }
        if (Client.Pending || Client.Opening) return;
        // This press is consumed by the normal placement loop. That loop rechecks
        // requirements and stamina and runs all normal successful-build bookkeeping.
        AccessTools.FieldRefAccess<Player, float>("m_placePressedTime")(player) = Time.time;
        replaying = true;
    }

    internal static void AfterUpdate(Player player)
    {
        if (player != Player.m_localPlayer || !replaying) return;
        // Cancel if the placement loop did not reach TryPlacePiece (e.g. stamina,
        // station or UI state changed). Accepted materials remain in the backpack.
        Reset();
        AccessTools.FieldRefAccess<Player, float>("m_placePressedTime")(player) = -9999f;
    }
}
