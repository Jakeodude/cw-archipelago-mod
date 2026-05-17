// Managers/DeathLinkManager.cs
// Manages incoming and outgoing Death Link events for the Archipelago session.

using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.BounceFeatures.DeathLink;
using ContentWarningArchipelago.Core;

namespace ContentWarningArchipelago.Managers
{
    public class DeathLinkManager
    {
        /// <summary>The active DeathLink service for the session; null when disconnected.</summary>
        public static DeathLinkService? DeathLinkService { get; private set; }

        /// <summary>
        /// Flag to prevent infinite death loops.
        /// Set to true when we receive a death link from the AP server and kill the player.
        /// The outgoing patch checks this flag and skips sending a death link if true,
        /// then resets it so the next genuine team wipe is sent normally.
        /// </summary>
        public static bool IsDyingFromAP = false;

        /// <summary>
        /// Initialize the DeathLink service and subscribe to incoming death link events.
        /// Called once after successful AP login if death_link is enabled in slot data.
        /// </summary>
        public static void Initialize(ArchipelagoSession session)
        {
            if (session == null)
            {
                Plugin.Logger.LogWarning("[DeathLink] Initialize called with null session — skipping.");
                return;
            }

            DeathLinkService = session.CreateDeathLinkService();
            DeathLinkService.OnDeathLinkReceived += ReceiveDeathLink;
            DeathLinkService.EnableDeathLink();

            Plugin.Logger.LogInfo("[DeathLink] DeathLinkService initialized and enabled.");
        }

        /// <summary>
        /// Handle an incoming death link from the AP server.
        /// Kills the local player if they are alive.
        /// </summary>
        private static void ReceiveDeathLink(DeathLink deathLink)
        {
            Plugin.Logger.LogInfo(
                $"[DeathLink] Received death link from '{deathLink.Source}': {deathLink.Cause}");

            // Prevent infinite loop: flag that we're dying from AP so the outgoing patch
            // knows not to send this death back to the server.
            IsDyingFromAP = true;

            // Kill the local player if they exist and aren't already dead.
            if (Player.localPlayer != null && !Player.localPlayer.data.dead)
            {
                Plugin.Logger.LogInfo("[DeathLink] Killing local player in response to incoming death link.");
                Player.localPlayer.Die();
            }
        }
    }
}
