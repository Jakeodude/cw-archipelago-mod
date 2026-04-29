// Patches/MetaProgressionHatPatch.cs
//
// Hijacks MetaProgressionHandler's hat-persistence and Meta Coin economy when
// an Archipelago session is active so that hat ownership and MC balance both
// become session-scoped (lobby-shared via DataStorage for MC) rather than
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
//
// PATCH 3 — AddMetaCoinsPatch         (issue #10)
// PATCH 4 — RemoveMetaCoinsPatch      (issue #10)
// PATCH 5 — UpdateAndSavePatch        (issue #10)
//
//   The AP mod owns the lobby-shared MC balance via the DataStorage key
//   `CW_MetaCoins_{slot}` (see ArchipelagoClient.InitMetaCoinsDataStorage).
//   These patches make the MC economy AP-authoritative:
//
//     • AddMetaCoins is fully suppressed in AP mode; ItemData.GrantMetaCoins
//       routes AP-item grants through ArchipelagoClient.AddMetaCoinsDelta
//       so the lobby key changes once and every client picks up the change
//       via the OnValueChanged listener.
//
//     • RemoveMetaCoins (called by HatShop.RPCA_BuyHat on every client) is
//       redirected: only the master client sends a `-price` op to DS;
//       non-master clients no-op.  Vanilla's local field write is skipped
//       so all clients converge on whatever DS reports back.
//
//     • UpdateAndSave is short-circuited so the listener-driven field
//       writes never bleed the AP balance into the player's vanilla
//       SaveSystem.SaveMetaData file.  Vanilla persistence resumes the
//       moment the player disconnects from AP.
// ─────────────────────────────────────────────────────────────────────────────

using ContentWarningArchipelago.Core;
using HarmonyLib;
using Photon.Pun;

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

    // =========================================================================
    // PATCH 3 — Suppress AddMetaCoins in AP mode (issue #10)
    //
    // In AP mode, MC grants from AP items are delivered exclusively through
    // ArchipelagoClient.AddMetaCoinsDelta in ItemData.GrantMetaCoins.  Any
    // other vanilla code path that calls AddMetaCoins must not mutate the
    // local field or trigger UpdateAndSave, since the lobby balance is owned
    // by the DataStorage listener.
    // =========================================================================
    [HarmonyPatch(typeof(MetaProgressionHandler), nameof(MetaProgressionHandler.AddMetaCoins))]
    internal static class AddMetaCoinsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(int amount)
        {
            if (!Plugin.connection.connected) return true; // vanilla path

            Plugin.Logger.LogDebug(
                $"[AddMetaCoinsPatch] AP active — vanilla AddMetaCoins({amount}) suppressed.");
            return false;
        }
    }

    // =========================================================================
    // PATCH 4 — Redirect RemoveMetaCoins to DataStorage (issue #10)
    //
    // HatShop.RPCA_BuyHat broadcasts via RpcTarget.All, so every connected
    // client runs RemoveMetaCoins(price) for every purchase.  In AP mode we
    // collapse that to a single DS write from the master client and let the
    // OnValueChanged listener fan the new balance back out to every client
    // (including the master, who applies it via reflection in
    // ArchipelagoClient.ApplyMetaCoinsLocally).
    // =========================================================================
    [HarmonyPatch(typeof(MetaProgressionHandler), nameof(MetaProgressionHandler.RemoveMetaCoins))]
    internal static class RemoveMetaCoinsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(int amount)
        {
            if (!Plugin.connection.connected) return true; // vanilla path

            if (PhotonNetwork.IsMasterClient)
            {
                Plugin.connection.AddMetaCoinsDelta(-amount);
                Plugin.Logger.LogInfo(
                    $"[RemoveMetaCoinsPatch] AP active — sent -{amount} MC to DataStorage.");
            }
            else
            {
                Plugin.Logger.LogDebug(
                    $"[RemoveMetaCoinsPatch] AP active — non-master client " +
                    $"swallowing local RemoveMetaCoins({amount}); listener will sync.");
            }

            return false; // skip vanilla body on every client
        }
    }

    // =========================================================================
    // PATCH 5 — Suppress UpdateAndSave in AP mode (issue #10)
    //
    // Defence in depth: any code path that survives the patches above and
    // still reaches UpdateAndSave (e.g., a future console command, or
    // SetMetaCoins called directly by the listener helper) must not write
    // the AP-time MC balance to the player's vanilla SaveSystem file.
    // ArchipelagoClient.ApplyMetaCoinsLocally writes the private field via
    // reflection precisely to avoid this path; this prefix is the safety net.
    // =========================================================================
    [HarmonyPatch(typeof(MetaProgressionHandler), "UpdateAndSave")]
    internal static class UpdateAndSavePatch
    {
        [HarmonyPrefix]
        private static bool Prefix() =>
            !Plugin.connection.connected; // false in AP mode → skip vanilla body
    }
}
