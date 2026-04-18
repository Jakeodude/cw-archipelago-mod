// Patches/DivingBellChargerPatch.cs
// Harmony prefix on Player.PlayerData.UpdateValues — modelled on
// BetterOxygen's ___isInDiveBell check and the Charging_Divebell mod's concept.
//
// When the "Diving Bell Charger" AP item has been received
// (APSave.saveData.diveBellChargerUnlocked == true), every battery-type item
// in the local player's inventory is recharged at a fixed rate while the
// player is standing inside the diving bell.
//
// HOW BATTERY CHARGING WORKS IN CONTENT WARNING
// Battery-powered items expose a BatteryEntry (or similar) on their
// ItemInstanceData.  We use reflection to find and write to known field names
// so this patch survives minor game updates without crashing at startup.

using System;
using System.Reflection;
using ContentWarningArchipelago.Core;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    [HarmonyPatch(typeof(Player.PlayerData))]
    internal static class DivingBellChargerPatch
    {
        // Seconds of charge restored per second while in the bell.
        private const float ChargeRate = 0.1f; // 10% per second (full charge in 10 s)

        // Cached reflection info for the battery field — resolved once on first use.
        private static FieldInfo? _batteryField;
        private static PropertyInfo? _chargeProp;
        private static bool _reflectionAttempted;

        [HarmonyPatch("UpdateValues")]
        [HarmonyPrefix]
        private static void Prefix(ref bool ___isInDiveBell)
        {
            if (!Plugin.connection.connected) return;
            if (!APSave.saveData.diveBellChargerUnlocked) return;
            if (!___isInDiveBell) return;

            // Only charge local player's inventory.
            var localPlayer = Player.localPlayer;
            if ((object)localPlayer == null) return;

            var inventory = localPlayer.GetComponent<PlayerInventory>();
            if ((object)inventory == null) return;

            // Attempt to resolve battery fields once.
            if (!_reflectionAttempted)
            {
                _reflectionAttempted = true;
                TryResolveBatteryReflection();
            }

            // Iterate all inventory slots and apply charge.
            foreach (var slot in inventory.slots)
            {
                if (slot == null || slot.ItemInSlot.item == null) continue;
                TryChargeItem(slot.ItemInSlot.data);
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Tries to charge one item's battery via the cached reflection info.
        /// Silently skips items that don't have a recognised battery field.
        /// </summary>
        private static void TryChargeItem(ItemInstanceData data)
        {
            if (data == null) return;

            try
            {
                // Strategy 1: known BatteryEntry field name on ItemInstanceData.
                if (_batteryField != null)
                {
                    object? battery = _batteryField.GetValue(data);
                    if (battery != null && _chargeProp != null)
                    {
                        float current = (float)(_chargeProp.GetValue(battery) ?? 0f);
                        _chargeProp.SetValue(battery, Mathf.Clamp01(current + ChargeRate * Time.deltaTime));
                        return;
                    }
                }

                // Strategy 2: generic scan for a float field named "charge" / "battery".
                foreach (var field in data.GetType().GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    string fn = field.Name.ToLowerInvariant();
                    if ((fn.Contains("charge") || fn.Contains("battery"))
                        && field.FieldType == typeof(float))
                    {
                        float current = (float)(field.GetValue(data) ?? 0f);
                        field.SetValue(data, Mathf.Clamp01(current + ChargeRate * Time.deltaTime));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug($"[DiveBellCharger] TryChargeItem exception: {ex.Message}");
            }
        }

        /// <summary>
        /// One-time reflection resolution for the BatteryEntry on ItemInstanceData.
        /// Looks for a field called "battery" / "m_battery" / "batteryEntry" that
        /// itself exposes a "charge" or "m_charge" float property or field.
        /// </summary>
        private static void TryResolveBatteryReflection()
        {
            try
            {
                Type? dataType = AccessTools.TypeByName("ItemInstanceData");
                if (dataType == null) return;

                foreach (string fname in new[] {
                    "battery", "m_battery", "batteryEntry",
                    "m_batteryEntry", "batteryData" })
                {
                    var f = AccessTools.Field(dataType, fname);
                    if (f == null) continue;

                    // Look for a charge property/field on the battery type.
                    foreach (string pname in new[] { "charge", "m_charge", "Charge", "value" })
                    {
                        var p = AccessTools.Property(f.FieldType, pname);
                        if (p != null && p.PropertyType == typeof(float))
                        {
                            _batteryField = f;
                            _chargeProp   = p;
                            Plugin.Logger.LogInfo(
                                $"[DiveBellCharger] Resolved battery: {fname}.{pname}");
                            return;
                        }
                    }
                }

                Plugin.Logger.LogDebug(
                    "[DiveBellCharger] Could not resolve battery field — " +
                    "falling back to per-item generic scan.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[DiveBellCharger] Reflection setup failed: {ex.Message}");
            }
        }
    }
}
