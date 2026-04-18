// Patches/ItemPickupPatch.cs
//
// Hooks into the Content Warning item-pickup / shop-purchase pipeline to fire
// Archipelago location checks.
//
// How item acquisition works in Content Warning (from Assembly-CSharp string scan):
//
//   SHOP PURCHASES
//   ─────────────
//   • ShopInteractableBuy  – MonoBehaviour on each "Buy" button in the shop.
//     The player clicks it → RPCA_AddItemToCart is called → item goes into
//     the ShoppingCart.  When the player confirms checkout, the actual item
//     is granted and the cart is cleared.
//   • We patch ShopInteractableBuy via AccessTools so the patch compiles even
//     when the exact overload name isn't known at compile time.
//
//   PHYSICAL ITEM PICKUP (dungeon / surface)
//   ────────────────────────────────────────
//   • PickupHandler.RPC_RequestPickup – the networked RPC fired on the host
//     when any player grabs a Pickup from the world.
//   • We patch this to handle pickup-based location checks (e.g., picking up
//     a specific piece of filming equipment triggers a location).
//
//   VIDEO UPLOAD / EXTRACTION
//   ─────────────────────────
//   • ExtractVideoMachine.EvaluateRecording – called on the host when the
//     footage upload machine finishes evaluating a recording.
//   • We patch this postfix to fire the "Any Extraction" and day-based checks.
//
// NOTE: All three patches are written with AccessTools string-based type and
// method resolution so they gracefully no-op (log a warning) if the game
// ever renames a method, rather than crashing on startup.

using System;
using System.Reflection;
using HarmonyLib;
using ContentWarningArchipelago.Data;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // ======================================================================
    // PATCH 1 — Shop item purchased
    // Target: ShopInteractableBuy  (the MonoBehaviour on each shop "Buy" button)
    // Method: Interact / Use / OnPointerClick — resolved at runtime via AccessTools.
    //
    // When triggered, we look at which ShopItem is associated with the interactable
    // and fire the matching "Bought <ItemName>" location check.
    // ======================================================================

    [HarmonyPatch]
    public static class ShopBuyPatch
    {
        // Tell Harmony which method to patch — resolved at runtime.
        static MethodBase? TargetMethod()
        {
            // "ShopInteractableBuy" confirmed present in Assembly-CSharp string scan.
            // The interactable system in CW uses an "Interact" convention.
            // We also try "Use" as a fallback.
            var type = AccessTools.TypeByName("ShopInteractableBuy");
            if (type == null)
            {
                Plugin.Logger.LogWarning("[ShopBuyPatch] Could not find type 'ShopInteractableBuy'. Patch skipped.");
                return null;
            }

            // Try known method names in priority order.
            foreach (string candidate in new[] { "Interact", "Use", "Buy", "OnClick", "Perform" })
            {
                var m = AccessTools.Method(type, candidate);
                if (m != null)
                {
                    Plugin.Logger.LogInfo($"[ShopBuyPatch] Patching {type.Name}.{m.Name}");
                    return m;
                }
            }

            Plugin.Logger.LogWarning("[ShopBuyPatch] No matching method found on ShopInteractableBuy. Patch skipped.");
            return null;
        }

        /// <summary>
        /// Postfix fires after the shop buy method completes.
        /// __instance is the ShopInteractableBuy MonoBehaviour; we read the
        /// associated ShopItem / item name via reflection and send the check.
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (!Plugin.connection.connected) return;

            try
            {
                // Try to get the item name from the ShopItem attached to this interactable.
                // Field/property names discovered from the assembly string scan:
                //   m_ShopHandler, CurrentSelectedShopItem, ShopItem, m_ItemNameText, etc.
                string? itemName = TryGetShopItemName(__instance);
                if (string.IsNullOrEmpty(itemName))
                {
                    Plugin.Logger.LogWarning("[ShopBuyPatch] Could not resolve shop item name from interactable.");
                    return;
                }

                string locationName = "Bought " + itemName;
                long locId = LocationData.GetId(locationName);
                if (locId < 0)
                {
                    // Item not in our location table — that's fine (non-AP items).
                    Plugin.Logger.LogDebug($"[ShopBuyPatch] '{locationName}' not an AP location.");
                    return;
                }

                Plugin.Logger.LogInfo($"[ShopBuyPatch] Sending check for: {locationName}");
                Plugin.SendCheck(locId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ShopBuyPatch] Exception in postfix: {ex}");
            }
        }

        /// <summary>
        /// Attempts to extract the item display name from the ShopInteractableBuy instance
        /// using a chain of reflection lookups for the most likely field/property names.
        /// Returns null if nothing resolves.
        /// </summary>
        private static string? TryGetShopItemName(object instance)
        {
            Type t = instance.GetType();

            // Strategy 1: direct "shopItem" or "m_ShopItem" field → ShopItem component.
            foreach (string fname in new[] { "shopItem", "m_ShopItem", "ShopItem", "item", "m_item" })
            {
                var field = AccessTools.Field(t, fname);
                if (field != null)
                {
                    var shopItem = field.GetValue(instance);
                    if (shopItem != null)
                    {
                        // ShopItem likely has a "name" field (Unity Object.name) or "itemName".
                        var nameField = AccessTools.Field(shopItem.GetType(), "itemName")
                                     ?? AccessTools.Field(shopItem.GetType(), "m_itemName");
                        if (nameField != null)
                            return nameField.GetValue(shopItem)?.ToString();

                        // Fallback to Unity Object.name via cast.
                        if (shopItem is UnityEngine.Object unityObj)
                            return unityObj.name;
                    }
                }
            }

            // Strategy 2: "m_ItemNameText" TMPro text field on the interactable itself.
            foreach (string fname in new[] { "m_itemNameText", "m_ItemNameText", "itemNameText" })
            {
                var field = AccessTools.Field(t, fname);
                if (field != null)
                {
                    var textComp = field.GetValue(instance);
                    if (textComp != null)
                    {
                        var textProp = AccessTools.Property(textComp.GetType(), "text");
                        if (textProp != null)
                            return textProp.GetValue(textComp)?.ToString();
                    }
                }
            }

            return null;
        }
    }

    // ======================================================================
    // PATCH 2 — Physical item picked up from the world
    // Target: PickupHandler.RPC_RequestPickup
    // Purpose: Detect when a player picks up a specific item (e.g. filming
    //          equipment) and fire the matching location check.
    //
    // Current apworld locations that map to physical pickups:
    //   • "Any Extraction" fires on footage upload, not item pickup.
    //   • Filming locations fire when a recording is evaluated.
    //   • This patch is a foundation for any future pickup-based locations
    //     (e.g. picking up a specific prop could be a location check).
    // ======================================================================

    [HarmonyPatch]
    public static class PickupPatch
    {
        static MethodBase? TargetMethod()
        {
            // "PickupHandler" and "RPC_RequestPickup" confirmed in string scan.
            var type = AccessTools.TypeByName("PickupHandler");
            if (type == null)
            {
                Plugin.Logger.LogWarning("[PickupPatch] Could not find type 'PickupHandler'. Patch skipped.");
                return null;
            }

            var method = AccessTools.Method(type, "RPC_RequestPickup");
            if (method == null)
            {
                Plugin.Logger.LogWarning("[PickupPatch] Could not find 'RPC_RequestPickup' on PickupHandler. Patch skipped.");
                return null;
            }

            Plugin.Logger.LogInfo($"[PickupPatch] Patching {type.Name}.{method.Name}");
            return method;
        }

        /// <summary>
        /// Postfix fires after a pickup has been successfully grabbed.
        /// __instance is the PickupHandler MonoBehaviour.
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (!Plugin.connection.connected) return;

            try
            {
                // Resolve the picked-up item's name from the PickupHandler.
                string? itemName = TryGetPickupName(__instance);
                if (string.IsNullOrEmpty(itemName))
                {
                    Plugin.Logger.LogDebug("[PickupPatch] Could not resolve pickup name.");
                    return;
                }

                Plugin.Logger.LogDebug($"[PickupPatch] Item picked up: {itemName}");

                // ---- "Any Extraction" check -------------------------------------------------
                // (Not directly a pickup — handled in the EvaluateRecording patch below.)

                // ---- Future: map specific pickup names to AP location IDs -----------------
                // Example: if itemName == "Reporter Mic" → "Filmed Reporter Mic" is an artifact
                // location, but that fires when you *film* it, not pick it up.
                //
                // For now this patch logs the pickup. Extend as new AP locations are defined.
                // ---------------------------------------------------------------------------
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[PickupPatch] Exception in postfix: {ex}");
            }
        }

        private static string? TryGetPickupName(object instance)
        {
            Type t = instance.GetType();

            // Try common field names for the item/pickup GameObject.
            foreach (string fname in new[] { "m_pickup", "pickup", "m_item", "item" })
            {
                var field = AccessTools.Field(t, fname);
                if (field == null) continue;
                var value = field.GetValue(instance);
                if (value is UnityEngine.Object unityObj)
                    return unityObj.name;
            }

            // Fallback: the handler itself might be on the pickup GameObject.
            if (instance is Component comp)
                return comp.gameObject.name;

            return null;
        }
    }

    // ======================================================================
    // PATCH 3 — Footage extracted / recording evaluated
    // Target: ExtractVideoMachine (state machine that handles the upload terminal)
    // Method: EvaluateRecording — confirmed in Assembly-CSharp string scan.
    //
    // Fires:
    //   • "Any Extraction"                   (offset 0)
    //   • "Extracted Footage on Day <N>"     (offsets 1–15)
    // ======================================================================

    [HarmonyPatch]
    public static class ExtractVideoMachinePatch
    {
        static MethodBase? TargetMethod()
        {
            // Try ExtractVideoMachine first, then UploadVideoStation as fallback.
            foreach (string typeName in new[] { "ExtractVideoMachine", "UploadVideoStation" })
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) continue;

                // EvaluateRecording is confirmed in the string scan.
                foreach (string methodName in new[] { "EvaluateRecording", "ExtractRecording", "ExtractVideo" })
                {
                    var m = AccessTools.Method(type, methodName);
                    if (m != null)
                    {
                        Plugin.Logger.LogInfo($"[ExtractPatch] Patching {type.Name}.{m.Name}");
                        return m;
                    }
                }
            }

            Plugin.Logger.LogWarning("[ExtractPatch] Could not find extraction method. Patch skipped.");
            return null;
        }

        /// <summary>
        /// Postfix fires after a recording has been evaluated/extracted.
        /// We send:
        ///   • "Any Extraction"
        ///   • "Extracted Footage on Day N" for whatever the current day is.
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (!Plugin.connection.connected) return;

            try
            {
                // ---- Any Extraction --------------------------------------------------------
                long anyExtractionId = LocationData.GetId(LocationNames.AnyExtraction);
                if (anyExtractionId >= 0)
                    Plugin.SendCheck(anyExtractionId);

                // ---- Day-based extraction --------------------------------------------------
                // Try to read the current day number from a known field.
                int day = TryGetCurrentDay(__instance);
                if (day > 0 && day <= 15)
                {
                    string dayLocName = LocationNames.ExtractedFootagePrefix + day;
                    long dayLocId = LocationData.GetId(dayLocName);
                    if (dayLocId >= 0)
                    {
                        Plugin.Logger.LogInfo($"[ExtractPatch] Day {day} extraction → {dayLocName}");
                        Plugin.SendCheck(dayLocId);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ExtractPatch] Exception in postfix: {ex}");
            }
        }

        /// <summary>
        /// Attempts to read the current in-game day from the extract machine or
        /// a related game manager. Returns 0 if unresolvable.
        /// </summary>
        private static int TryGetCurrentDay(object instance)
        {
            // Look for "day", "currentDay", "dayNumber" etc. on the instance.
            Type t = instance.GetType();
            foreach (string fname in new[] { "day", "currentDay", "dayNumber", "m_day", "m_currentDay" })
            {
                var field = AccessTools.Field(t, fname);
                if (field != null)
                {
                    var val = field.GetValue(instance);
                    if (val is int i) return i;
                }
            }

            // Fallback: look for a static GameManager / SurfaceNetworkHandler day field.
            foreach (string typeName in new[] { "SurfaceNetworkHandler", "GameDirector", "GameManager" })
            {
                var mgr = AccessTools.TypeByName(typeName);
                if (mgr == null) continue;

                foreach (string fname in new[] { "day", "currentDay", "dayNumber" })
                {
                    // Static field first.
                    var sf = AccessTools.Field(mgr, fname);
                    if (sf != null && sf.IsStatic)
                    {
                        var val = sf.GetValue(null);
                        if (val is int i) return i;
                    }

                    // Instance singleton: look for a static "instance" field.
                    var instField = AccessTools.Field(mgr, "instance");
                    if (instField?.IsStatic == true)
                    {
                        var singleton = instField.GetValue(null);
                        if (singleton != null)
                        {
                            var dayField = AccessTools.Field(mgr, fname);
                            if (dayField != null)
                            {
                                var val = dayField.GetValue(singleton);
                                if (val is int i) return i;
                            }
                        }
                    }
                }
            }

            return 0;
        }
    }
}
