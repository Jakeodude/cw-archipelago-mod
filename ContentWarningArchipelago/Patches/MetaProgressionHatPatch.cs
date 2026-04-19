// Patches/MetaProgressionHatPatch.cs
//
// Hijacks MetaProgressionHandler's hat-persistence logic when an Archipelago
// session is active so that hat ownership becomes session-scoped rather than
// persisted to the game's native save file.
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 1 — GetUnlockedHatsPatch
//   Target : MetaProgressionHandler.GetUnlockedHats()   [public static]
//
//   In vanilla, HatBuyInteractable.IsOwned calls:
//       MetaProgressionHandler.GetUnlockedHats().Any(o => o == ihat.runtimeHatIndex)
//   By replacing the return value with APSave.saveData.sessionUnlockedHats when
//   AP is active, the shop shows all hats as "unowned" at the start of each
//   session and only reveals those bought during this run.
//
// PATCH 2 — UnlockHatPatch
//   Target : MetaProgressionHandler.UnlockHat(int hat)  [public static]
//
//   Vanilla writes the hat to the native save file (UpdateAndSave via the
//   private unlockedHats HashSet).  In AP mode we intercept this, skip the
//   disk write, and instead add the hat index to sessionUnlockedHats.
//   This covers both:
//     • HatShop.RPCA_BuyHat  (buying from the shop)
//     • HatItem.Update        (equipping a hat item from inventory)
// ─────────────────────────────────────────────────────────────────────────────

using ContentWarningArchipelago.Core;
using HarmonyLib;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // PATCH 1 — Override GetUnlockedHats to return only session hats in AP mode
    // =========================================================================
    [HarmonyPatch(typeof(MetaProgressionHandler), nameof(MetaProgressionHandler.GetUnlockedHats))]
    internal static class GetUnlockedHatsPatch
    {
        /// <summary>
        /// Postfix: when an AP session is active, replace the native hat list
        /// with the current session's purchased hats.
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(ref int[] __result)
        {
            if (!Plugin.connection.connected) return;

            // Return only the hats bought during this AP session.
            // sessionUnlockedHats is [JsonIgnore] so it starts empty on every connect.
            int[] sessionHats = new int[APSave.saveData.sessionUnlockedHats.Count];
            APSave.saveData.sessionUnlockedHats.CopyTo(sessionHats);
            __result = sessionHats;

            Plugin.Logger.LogDebug(
                $"[GetUnlockedHatsPatch] AP active — returning {sessionHats.Length} session hat(s).");
        }
    }

    // =========================================================================
    // PATCH 2 — Redirect UnlockHat to session storage in AP mode
    // =========================================================================
    [HarmonyPatch(typeof(MetaProgressionHandler), nameof(MetaProgressionHandler.UnlockHat))]
    internal static class UnlockHatPatch
    {
        /// <summary>
        /// Prefix: if AP is active, prevent the permanent disk unlock and store
        /// the hat index in <c>APSave.saveData.sessionUnlockedHats</c> instead.
        /// Returns <c>false</c> to skip the original method entirely.
        /// Returns <c>true</c> to let vanilla behavior run when AP is not active.
        /// </summary>
        [HarmonyPrefix]
        private static bool Prefix(int hat)
        {
            if (!Plugin.connection.connected) return true; // vanilla path

            // AP mode: add to session set, skip native save.
            bool added = APSave.saveData.sessionUnlockedHats.Add(hat);
            if (added)
            {
                Plugin.Logger.LogInfo(
                    $"[UnlockHatPatch] AP active — hat {hat} added to session (no disk write).");
            }

            return false; // skip MetaProgressionHandler.UnlockHat body
        }
    }
}
