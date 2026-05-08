// Patches/DivingBellRechargePatch.cs
// Harmony Postfix on Player.Update.
//
// When the "Diving Bell Charger" AP item has been received
// (APSave.saveData.diveBellChargerUnlocked == true), every battery-type item
// in the LOCAL player's inventory is recharged at 5% of its max charge per
// second while the player is standing inside the diving bell.
//
// HOW BATTERY CHARGING WORKS IN CONTENT WARNING
// Battery-powered items store their charge in a BatteryEntry (an ItemDataEntry
// subclass on ItemInstanceData).  ItemInstanceData exposes
// TryGetEntry<BatteryEntry>() for exactly this pattern — no reflection needed.
// BatteryEntry.AddCharge() already clamps the result to [0, m_maxCharge].
//
// This mirrors the game's own BatteryRechargeStation.LateUpdate() exactly,
// replacing the old DivingBellChargerPatch that incorrectly tried to find
// "battery" / "m_battery" fields directly on ItemInstanceData (which don't
// exist) and generated AccessTools.Field warnings as a result.

using ContentWarningArchipelago.Core;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    [HarmonyPatch(typeof(Player), nameof(Player.Update))]
    internal static class DivingBellRechargePatch
    {
        // Fraction of an item's m_maxCharge restored per second while in the bell.
        // 0.05 = 5 % / s  →  full charge from empty in 20 s.
        private const float ChargeRatePerSecond = 0.05f;

        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            // ── Guard: only run for the local player ──────────────────────────
            if (!__instance.IsLocal) return;

            // ── Guard: AP connection and upgrade unlock ───────────────────────
            if (!Plugin.connection.connected) return;
            if (!APSave.saveData.diveBellChargerUnlocked) return;

            // ── Guard: player must be inside the diving bell ──────────────────
            if (!__instance.data.isInDiveBell) return;

            // ── Get inventory ─────────────────────────────────────────────────
            if (!__instance.TryGetInventory(out PlayerInventory inventory)) return;

            // ── Recharge every battery item in the inventory ──────────────────
            // Mirrors BatteryRechargeStation.LateUpdate() exactly:
            //   slot.ItemInSlot.data.TryGetEntry<BatteryEntry>(out var t)
            //   t.AddCharge(rechargeRate * Time.deltaTime)
            float chargeToAdd = ChargeRatePerSecond * Time.deltaTime;

            foreach (InventorySlot slot in inventory.slots)
            {
                if (slot == null || slot.ItemInSlot.item == null) continue;

                ItemInstanceData data = slot.ItemInSlot.data;
                if (data == null) continue;

                // TryGetEntry<BatteryEntry> returns false for items with no battery —
                // safely skips cameras, emote items, artifacts, etc.
                if (!data.TryGetEntry<BatteryEntry>(out BatteryEntry battery)) continue;

                // Skip items that are already at or above max charge.
                if (battery.m_maxCharge <= battery.m_charge) continue;

                // AddCharge clamps the result to [0, m_maxCharge] internally.
                battery.AddCharge(chargeToAdd * battery.m_maxCharge);

                Plugin.Logger.LogDebug(
                    $"[DiveBellCharger] Charged {slot.ItemInSlot.item.name}: " +
                    $"{battery.m_charge:F1}/{battery.m_maxCharge:F1} " +
                    $"({battery.GetPercentage() * 100f:F0}%)");
            }
        }
    }
}
