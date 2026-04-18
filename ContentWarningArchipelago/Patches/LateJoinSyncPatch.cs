// Patches/LateJoinSyncPatch.cs
// Postfix on PlayerHandler.AddPlayer — modelled directly on
// Virality/Patches/PlayerHandlerPatches.cs.
//
// Virality's pattern:
//   • Check PhotonNetwork.IsMasterClient before doing anything authoritative.
//   • Skip the local player (player.IsLocal).
//   • Send targeted RPCs via existing PhotonViews (SurfaceNetworkHandler.Instance.m_View).
//
// For the AP mod, when a new player joins mid-run the master client needs to
// tell them which AP-driven world upgrades are currently active.  Specifically:
//   • Whether the Diving Bell Charger is unlocked (affects all players' inventory)
//   • Whether the Diving Bell O2 Refill is unlocked (affects all players)
//   • The current pending money amount (so non-host can queue their portion)
//
// We send these as Photon Custom Room Properties (the lightest approach — no
// new PhotonView required) via PhotonNetwork.CurrentRoom.SetCustomProperties.
// The late-joining client reads them back on join via the same key.
//
// NOTE: Player-specific AP state (oxygen level, camera level, etc.) lives in each
// player's own APSaveData and is correctly replayed when they reconnect to the
// AP server — this patch handles only the SHARED / WORLD-STATE unlocks.

using ContentWarningArchipelago.Core;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Syncs AP world-state to late-joining players.
    /// Modelled on Virality's PlayerHandler.AddPlayer postfix pattern.
    /// </summary>
    [HarmonyPatch(typeof(PlayerHandler))]
    [HarmonyPriority(Priority.Normal)]
    internal static class LateJoinSyncPatch
    {
        // Photon Custom Room Property keys.
        internal const string KeyDiveBellCharger = "AP_DBC";
        internal const string KeyDiveBellO2      = "AP_O2";
        internal const string KeyPendingMoney    = "AP_PM";

        // ---- Outbound (master client → joining player) --------------------------

        /// <summary>
        /// When a new player joins, master client broadcasts current AP
        /// world-state as Custom Room Properties so all clients (including the
        /// late-joiner) can read them.
        /// Mirrors Virality's AddPlayerPostfixSyncObjective pattern.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(PlayerHandler.AddPlayer))]
        private static void AddPlayerPostfix(Player player)
        {
            // Only the master client publishes state (Virality pattern).
            if (!PhotonNetwork.IsMasterClient) return;

            // Don't sync to ourselves.
            if (player.IsLocal) return;

            if (!Plugin.connection.connected) return;

            Plugin.Logger.LogInfo(
                $"[LateJoinSync] New player '{player.refs.view.Controller.NickName}' joined — broadcasting AP world state.");

            BroadcastWorldState();
        }

        // ---- Called whenever our own state changes (e.g., new item received) ---

        /// <summary>
        /// Publish current AP world-state to Custom Room Properties so all
        /// connected clients (including any already-connected players) are current.
        /// Call this from ItemData.HandleReceivedItem whenever a world-state item
        /// is received (dive bell charger, O2 refill).
        /// </summary>
        internal static void BroadcastWorldState()
        {
            if (!PhotonNetwork.IsMasterClient) return;

            var props = new Hashtable
            {
                [KeyDiveBellCharger] = APSave.saveData.diveBellChargerUnlocked,
                [KeyDiveBellO2]      = APSave.saveData.diveBellO2Unlocked,
                [KeyPendingMoney]    = APSave.saveData.pendingMoney,
            };

            PhotonNetwork.CurrentRoom?.SetCustomProperties(props);

            Plugin.Logger.LogDebug("[LateJoinSync] Room properties updated with AP world state.");
        }

        // ---- Inbound (any client reads room properties on join / change) --------

        /// <summary>
        /// Reads AP world-state from Custom Room Properties.
        /// Call this once after connecting to Photon (e.g., when the local player
        /// enters a room) to pick up state broadcast by the master client.
        /// </summary>
        internal static void ApplyRoomProperties()
        {
            var props = PhotonNetwork.CurrentRoom?.CustomProperties;
            if (props == null) return;

            if (props.TryGetValue(KeyDiveBellCharger, out object dbc) && dbc is bool dbcVal)
            {
                if (dbcVal && !APSave.saveData.diveBellChargerUnlocked)
                {
                    APSave.saveData.diveBellChargerUnlocked = true;
                    Plugin.Logger.LogInfo("[LateJoinSync] Applied DiveBellCharger from room properties.");
                }
            }

            if (props.TryGetValue(KeyDiveBellO2, out object o2) && o2 is bool o2Val)
            {
                if (o2Val && !APSave.saveData.diveBellO2Unlocked)
                {
                    APSave.saveData.diveBellO2Unlocked = true;
                    Plugin.Logger.LogInfo("[LateJoinSync] Applied DiveBellO2 from room properties.");
                }
            }

            // Pending money is additive — only the master client's copy matters,
            // but we note it locally for informational purposes.
            if (props.TryGetValue(KeyPendingMoney, out object pm) && pm is int pmVal && pmVal > 0)
            {
                Plugin.Logger.LogDebug($"[LateJoinSync] Room has {pmVal} pending AP money (master will apply).");
            }

            APSave.Flush();
        }
    }

    // =========================================================================
    // SurfaceNetworkHandler patch — apply room properties when the surface
    // scene loads (i.e., when the player enters/re-enters the hub).
    // Mirrors Virality's SurfaceNetworkHandlerPatches.RPCM_StartGame pattern.
    // =========================================================================

    [HarmonyPatch(typeof(SurfaceNetworkHandler))]
    [HarmonyPriority(Priority.Normal)]
    internal static class SurfaceApplyRoomPropsPatch
    {
        /// <summary>
        /// After the game starts / the surface scene is ready, apply any AP
        /// world-state that the master client published before we joined.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(SurfaceNetworkHandler.RPCM_StartGame))]
        private static void RPCM_StartGamePostfix()
        {
            if (!Plugin.connection.connected) return;

            LateJoinSyncPatch.ApplyRoomProperties();

            // If we're the master client, immediately broadcast our state too.
            if (PhotonNetwork.IsMasterClient)
                LateJoinSyncPatch.BroadcastWorldState();
        }
    }
}
