// Patches/ViewsMultiplierPatch.cs
// Postfix on BigNumbers.GetScoreToViews — applies the "Progressive Views"
// AP item multiplier to the global score→views conversion.
//
// WHAT THIS PATCHES
// BigNumbers.GetScoreToViews(int quota, int day) is the single method that
// converts raw footage score into view counts.  It is called by:
//   • UI_Views.Update() — the quota/views HUD bar
//   • The extraction machine when evaluating footage
// Patching this one method covers all view displays and quota calculations.
//
// MULTIPLIER FORMULA
// Each "Progressive Views" copy multiplies by 1.1×.
// At level 12 (max) the multiplier is 1.1^12 ≈ 3.14× vanilla views.
//
// Modelled on the BigNumbers reference from UI_Views.cs which confirms the
// signature: static int GetScoreToViews(int quota, int day).

using System;
using ContentWarningArchipelago.Core;
using HarmonyLib;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Multiplies the score→views result by 1.1 for each "Progressive Views"
    /// AP item received.  Applied globally so all UI and quota logic reflects
    /// the bonus.
    /// </summary>
    [HarmonyPatch(typeof(BigNumbers), nameof(BigNumbers.GetScoreToViews))]
    internal static class ViewsMultiplierPatch
    {
        private const double MultiplierPerLevel = 1.1;

        [HarmonyPostfix]
        private static void Postfix(ref int __result)
        {
            if (!Plugin.connection.connected) return;

            int level = APSave.saveData.viewsMultiplierLevel;
            if (level <= 0) return;

            double multiplier = Math.Pow(MultiplierPerLevel, level);
            __result = (int)(__result * multiplier);
        }
    }
}
