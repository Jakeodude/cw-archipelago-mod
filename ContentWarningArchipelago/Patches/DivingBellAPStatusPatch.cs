// Patches/DivingBellAPStatusPatch.cs
// Postfix on DivingBellSuitCellUI.Set — modelled directly on
// Better_Diving_Bell_UI/Patches/DivingBellSuitCellUIPatch.cs.
//
// That mod accesses __instance.m_oxygenText directly and writes to its .text
// and .color properties.  We do the same, appending an AP status line below
// the existing oxygen text so the player can see:
//   • Whether the AP session is connected
//   • The name of the last item they received from AP
//
// This gives useful in-game feedback without creating any new UI elements —
// we reuse the already-rendered text field the game already manages.

using ContentWarningArchipelago.Core;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Appends Archipelago connection status and last received item to the
    /// diving bell suit cell text (the oxygen % readout on the bell's UI panel).
    /// Modelled on Better_Diving_Bell_UI's DivingBellSuitCellUIPatch.
    /// </summary>
    [HarmonyPatch(typeof(DivingBellSuitCellUI))]
    internal static class DivingBellAPStatusPatch
    {
        // The last item name received from AP — updated by ItemData.HandleReceivedItem.
        internal static string LastItemName = "";

        [HarmonyPostfix]
        [HarmonyPatch(nameof(DivingBellSuitCellUI.Set))]
        private static void Set(Player player, float dst, DivingBellSuitCellUI __instance)
        {
            // Only annotate the local player's own cell.
            if (!player.IsLocal) return;

            try
            {
                bool connected = Plugin.connection.connected;

                // Build the AP status line.
                string statusLine = connected
                    ? $"<size=70%><color=#00FF88>[AP] Connected</color></size>"
                    : $"<size=70%><color=#FF4444>[AP] Disconnected</color></size>";

                if (connected && !string.IsNullOrEmpty(LastItemName))
                    statusLine += $"\n<size=60%>Last item: {LastItemName}</size>";

                // Append below the existing oxygen text.
                // Better_Diving_Bell_UI writes directly to m_oxygenText.text;
                // we follow the same pattern, preserving the original text.
                string original = __instance.m_oxygenText.text;

                // Avoid double-appending: strip any previous AP line.
                int apMarker = original.IndexOf("\n<size=70%><color=#");
                if (apMarker >= 0)
                    original = original.Substring(0, apMarker);

                __instance.m_oxygenText.text = original + "\n" + statusLine;
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogDebug($"[DiveBellAPStatus] Postfix exception: {ex.Message}");
            }
        }
    }
}
