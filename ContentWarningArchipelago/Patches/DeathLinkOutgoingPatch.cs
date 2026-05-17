// Patches/DeathLinkOutgoingPatch.cs
// Harmony patch that hooks into Player.RPCA_PlayerDie to send outgoing death links
// when the entire squad wipes during a dive.

using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using ContentWarningArchipelago.Managers;
using HarmonyLib;
using Photon.Pun;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Postfix patch on Player.RPCA_PlayerDie.
    /// Fires when any player dies, checks if the entire squad is wiped,
    /// and sends a death link to the Archipelago server if conditions are met.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.RPCA_PlayerDie))]
    public class DeathLinkOutgoingPatch
    {
        static void Postfix()
        {
            // 1. Safety check: Is DeathLink even enabled?
            if (DeathLinkManager.DeathLinkService == null)
                return;

            // 2. DIVE CHECK: If on surface/in house, ignore deaths
            // When players are on the surface, SurfaceNetworkHandler.Instance is not null.
            // When underground (in a dive), SurfaceNetworkHandler.Instance is null.
            // We only send death links for dives, not surface deaths.
            if (SurfaceNetworkHandler.Instance != null)
                return;

            // 3. Is the whole squad dead? (Team Wipe check)
            if (PlayerHandler.instance.playersAlive.Count > 0)
                return;

            // 4. Only the lobby host sends the message to avoid spam
            if (!PhotonNetwork.IsMasterClient)
                return;

            // 5. Prevent infinite loops from AP incoming links
            if (DeathLinkManager.IsDyingFromAP)
            {
                DeathLinkManager.IsDyingFromAP = false;
                return;
            }

            // 6. Send the death link!
            string cause = "The entire SpöökTube crew became content!";
            var deathLink = new DeathLink(Plugin.apSlot, cause);
            DeathLinkManager.DeathLinkService.SendDeathLink(deathLink);

            Plugin.Logger.LogInfo(
                $"[DeathLink] Team wipe detected during dive → sending death link to Archipelago.");
        }
    }
}
