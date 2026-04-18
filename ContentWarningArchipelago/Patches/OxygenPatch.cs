// Patches/OxygenPatch.cs
// Harmony prefix on Player.PlayerData.UpdateValues — modelled directly on
// BetterOxygen/Patches/PLayerDataPatch.cs.
//
// Implements two Archipelago items in a single patch to avoid paying the
// BetterOxygen approach of reading static config values; we read from
// APSave.saveData instead so the values change at runtime as items arrive.
//
// ---- Progressive Oxygen ----
//   Each copy received increments APSave.saveData.oxygenUpgradeLevel (max 4).
//   Each level adds 60 s to the base maximum of ~500 s (8 min 20 s).
//   Level 0 = vanilla 500 s, Level 4 = 740 s (~12 min 20 s).
//
// ---- Diving Bell O2 Refill ----
//   Once APSave.saveData.diveBellO2Unlocked is true, oxygen refills at
//   6 s/s while the player is inside the diving bell — the same behaviour
//   as BetterOxygen's `refillOxygen` option, gated on the AP unlock instead
//   of a config bool.

using ContentWarningArchipelago.Core;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Patches <c>Player.PlayerData.UpdateValues</c> to scale oxygen capacity
    /// based on received AP items and to enable dive-bell O2 refill.
    /// </summary>
    [HarmonyPatch(typeof(Player.PlayerData))]
    internal static class OxygenPatch
    {
        // How many seconds are added to maxOxygen per Progressive Oxygen level.
        private const float OxygenPerLevel = 60f;

        // Seconds of oxygen gained per second while standing in the dive bell
        // (mirrors BetterOxygen's default oxygenRefillRate of 6).
        private const float RefillRate = 6f;

        [HarmonyPatch("UpdateValues")]
        [HarmonyPrefix]
        private static void Prefix(
            ref float ___maxOxygen,
            ref float ___remainingOxygen,
            ref bool  ___isInDiveBell)
        {
            // Only act if we're connected to an AP session.
            if (!Plugin.connection.connected) return;

            var save = APSave.saveData;

            // ---- Progressive Oxygen: scale maxOxygen ----------------------------
            // Vanilla maxOxygen is set elsewhere; we raise it by the upgrade level.
            // We add the bonus on top of whatever the vanilla value is, capping at
            // the vanilla value + 4 × 60 s so we don't infinitely inflate it each frame.
            float bonus = save.oxygenUpgradeLevel * OxygenPerLevel;
            if (bonus > 0f)
            {
                // BetterOxygen assigns maxOxygen = btsMax directly.
                // We follow the same pattern but add on top of the vanilla cap.
                ___maxOxygen += bonus;
            }

            // ---- Diving Bell O2 Refill ------------------------------------------
            // Mirrors BetterOxygen's diveRefill block exactly.
            if (save.diveBellO2Unlocked && ___isInDiveBell)
            {
                ___remainingOxygen += RefillRate * Time.deltaTime;
                // Clamp so we never exceed the (possibly upgraded) maximum.
                if (___remainingOxygen > ___maxOxygen)
                    ___remainingOxygen = ___maxOxygen;
            }
        }
    }
}
