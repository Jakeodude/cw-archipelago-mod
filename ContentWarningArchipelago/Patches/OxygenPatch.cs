// Patches/OxygenPatch.cs
// Harmony patches for Progressive Oxygen and Diving Bell O2 Refill.
// Modelled on BetterOxygen/Patches/PLayerDataPatch.cs with the following
// critical corrections:
//
// BUG FIX — maxOxygen must be ASSIGNED, not incremented (+= bonus).
// The original code did `___maxOxygen += bonus` on every frame, causing the
// effective cap to grow by 60 s per frame at level 1.  After a few seconds the
// cap was thousands of seconds while remainingOxygen stayed at its real value,
// making the UI show ~5% and preventing death (remainingOxygen never reached 0
// relative to the ever-growing cap).
//
// The correct pattern (confirmed from BetterOxygen line 34: `___maxOxygen = btsMax`)
// is a stable assignment every frame:
//   ___maxOxygen = VanillaMaxOxygen + level × OxygenPerLevel
//
// The vanilla oxygen cap is 500 s (8 min 20 s).  BetterOxygen references this
// constant explicitly in its proportional-adjust logic (lines 45, 50).
//
// ---- Progressive Oxygen ----
//   Vanilla cap : 500 s
//   Each level  : +60 s   (up to 4 levels → max 740 s)
//   Formula     : maxOxygen = 500 + oxygenUpgradeLevel × 60
//
// ---- OxygenInitPatch (Player.Awake) ----
//   On spawn, sets both maxOxygen and remainingOxygen to the fully-upgraded
//   cap so the player starts with a complete tank rather than the vanilla 500 s.
//
// ---- ApplyOxygenUpgrade (mid-game sync) ----
//   Called from ItemData.HandleReceivedItem when Progressive Oxygen arrives
//   while the player is already underground.  Preserves the player's current
//   oxygen percentage when the cap grows (e.g., 80% of 500 → 80% of 560).
//
// ---- Diving Bell O2 Refill ----
//   Unchanged: +6 s/s while isInDiveBell, clamped to maxOxygen.

using System.Reflection;
using ContentWarningArchipelago.Core;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // UpdateValues PATCH  (every-frame oxygen cap override)
    // =========================================================================

    /// <summary>
    /// Prefix on <c>Player.PlayerData.UpdateValues</c>.
    /// Stably ASSIGNS <c>maxOxygen</c> to <c>VanillaMaxOxygen + bonus</c> each
    /// frame and applies dive-bell O2 refill when unlocked.
    /// </summary>
    [HarmonyPatch(typeof(Player.PlayerData))]
    internal static class OxygenPatch
    {
        /// <summary>Vanilla oxygen cap in seconds (confirmed from BetterOxygen).</summary>
        internal const float VanillaMaxOxygen = 500f;

        /// <summary>Seconds of bonus oxygen granted per Progressive Oxygen level.</summary>
        private const float OxygenPerLevel = 60f;

        /// <summary>Seconds of oxygen refilled per second while in the dive bell.</summary>
        private const float RefillRate = 6f;

        [HarmonyPatch("UpdateValues")]
        [HarmonyPrefix]
        private static void Prefix(
            ref float ___maxOxygen,
            ref float ___remainingOxygen,
            ref bool  ___isInDiveBell)
        {
            if (!Plugin.connection.connected) return;

            var save  = APSave.saveData;
            float bonus = save.oxygenUpgradeLevel * OxygenPerLevel;

            // ---- Progressive Oxygen: stable assignment (not +=) ----
            // BetterOxygen pattern: ASSIGN the new cap every frame so it is
            // always exactly VanillaMax + bonus, never growing unboundedly.
            if (bonus > 0f)
                ___maxOxygen = VanillaMaxOxygen + bonus;

            // ---- Diving Bell O2 Refill ----
            if (save.diveBellO2Unlocked && ___isInDiveBell)
            {
                ___remainingOxygen += RefillRate * Time.deltaTime;
                if (___remainingOxygen > ___maxOxygen)
                    ___remainingOxygen = ___maxOxygen;
            }
        }

        // =====================================================================
        // MID-GAME SYNC HELPER
        // Called from ItemData.HandleReceivedItem immediately after incrementing
        // oxygenUpgradeLevel so the player's current percentage is preserved.
        // =====================================================================

        /// <summary>
        /// Scales the local player's current oxygen proportionally when a new
        /// Progressive Oxygen level is received mid-game.
        /// <para>
        /// Example: player is at 400/500 s (80%) when level 1 arrives.
        /// New max = 560 s.  This method sets remainingOxygen = 0.80 × 560 = 448 s
        /// so the UI bar stays at 80% rather than jumping down.
        /// </para>
        /// </summary>
        /// <param name="newLevel">
        /// The <c>oxygenUpgradeLevel</c> value <em>after</em> it was incremented.
        /// </param>
        public static void ApplyOxygenUpgrade(int newLevel)
        {
            if (Player.localPlayer == null || Player.localPlayer.data == null)
            {
                Plugin.Logger.LogDebug(
                    "[OxygenPatch] ApplyOxygenUpgrade: localPlayer not present — " +
                    "full tank will be applied on next Player.Awake.");
                return;
            }

            var dataType = Player.localPlayer.data.GetType();

            var maxOxyField       = AccessTools.Field(dataType, "maxOxygen");
            var remainingOxyField = AccessTools.Field(dataType, "remainingOxygen");

            if (maxOxyField == null || remainingOxyField == null)
            {
                Plugin.Logger.LogWarning(
                    "[OxygenPatch] ApplyOxygenUpgrade: could not find " +
                    "'maxOxygen'/'remainingOxygen' fields on PlayerData.");
                return;
            }

            float oldMax       = VanillaMaxOxygen + (newLevel - 1) * OxygenPerLevel;
            float newMax       = VanillaMaxOxygen + newLevel * OxygenPerLevel;
            float currentRem   = (float)remainingOxyField.GetValue(Player.localPlayer.data);

            // Preserve current percentage (clamp to [0,1] to be safe).
            float pct          = oldMax > 0f ? Mathf.Clamp01(currentRem / oldMax) : 1f;
            float newRemaining = pct * newMax;

            maxOxyField.SetValue(Player.localPlayer.data, newMax);
            remainingOxyField.SetValue(Player.localPlayer.data, newRemaining);

            Plugin.Logger.LogInfo(
                $"[OxygenPatch] ApplyOxygenUpgrade: level {newLevel} — " +
                $"maxOxygen {oldMax} → {newMax} s, " +
                $"remainingOxygen {currentRem:F1} → {newRemaining:F1} s ({pct:P0}).");
        }
    }

    // =========================================================================
    // SPAWN INIT PATCH  (Player.Awake)
    // =========================================================================

    /// <summary>
    /// Postfix on <c>Player.Awake</c>.
    /// Sets both <c>maxOxygen</c> and <c>remainingOxygen</c> to the fully-
    /// upgraded cap on spawn so the player starts with a complete tank.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Awake")]
    internal static class OxygenInitPatch
    {
        [HarmonyPostfix]
        // ReSharper disable once InconsistentNaming
        private static void Postfix(Player __instance)
        {
            if (!Plugin.connection.connected) return;
            if (!__instance.IsLocal) return;

            int level = APSave.saveData.oxygenUpgradeLevel;
            if (level <= 0) return;

            float newMax = OxygenPatch.VanillaMaxOxygen + level * 60f;

            var dataType = __instance.data.GetType();
            var maxOxyField       = AccessTools.Field(dataType, "maxOxygen");
            var remainingOxyField = AccessTools.Field(dataType, "remainingOxygen");

            if (maxOxyField == null || remainingOxyField == null)
            {
                Plugin.Logger.LogWarning(
                    "[OxygenInitPatch] Could not find oxygen fields on PlayerData — " +
                    "spawn init skipped.");
                return;
            }

            maxOxyField.SetValue(__instance.data, newMax);
            remainingOxyField.SetValue(__instance.data, newMax);   // full tank

            Plugin.Logger.LogInfo(
                $"[OxygenInitPatch] Player.Awake — maxOxygen = remainingOxygen = " +
                $"{newMax} s (level {level}).");
        }
    }
}
