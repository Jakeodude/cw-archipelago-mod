// Patches/ItemPickupPatch.cs
//
// Hooks into the Content Warning item-pickup / shop-purchase / filming pipeline
// to fire Archipelago location checks.
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 1 — ShopBuyPatch
//   Target : ShopHandler.RPCA_SpawnDrone(byte[] itemIDs)   [private PunRPC]
//
//   WHY THIS METHOD:
//   The shop purchase flow is:
//     ShopInteractibleOrder.Interact()
//       → ShopHandler.OnOrderCartClicked()          [master client only]
//         → ShopHandler.BuyItem(ShoppingCart cart)  [master only; checks IsEmpty]
//           → private BuyItem(int cost, byte[], …)  [checks CanAfford]
//             → RPCA_SpawnDrone(byte[] itemIDs)     ← we patch here
//               (broadcast to ALL via RpcTarget.All ONLY when purchase succeeds)
//
//   RPCA_SpawnDrone is called exactly once per successful purchase, inside the
//   `if (m_RoomStats.CanAfford(cost))` block.  Because it is broadcast with
//   RpcTarget.All, all clients receive it; we guard with IsMasterClient so only
//   the host sends the AP check (one check per purchase, not one per player).
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 2 — PickupPatch
//   Target : Pickup.RPC_RequestPickup
//   Purpose: Foundation for future physical-pickup location checks.
//            Currently logs item name; no AP checks fire here yet.
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 3 — ContentEvaluatorPatch
//   Target : ContentEvaluator.EvaluateRecording(CameraRecording, out ContentBuffer)
//
//   WHY THIS METHOD (from VideoDebugPage.cs reference):
//     if (ContentEvaluator.EvaluateRecording(recording, out var buffer)) { … }
//   This is the single evaluation entry point called by the ExtractVideoMachine's
//   state machine when it finishes processing a disk.  It:
//     • Returns true when a valid recording was evaluated.
//     • Outputs a ContentBuffer whose frames describe every entity/event
//       captured on camera (monsters, artifacts, etc.).
//
//   The patch fires in the postfix (after evaluation is complete):
//     a) "Any Extraction"          — fires once per successful extraction
//     b) "Extracted Footage on Day N" — fires for the current in-game day
//     c) "Filmed <Monster/Artifact>"  — fires for each entity type found in
//        the ContentBuffer, looked up in MonsterFilmingData.
//
//   The ExtractVideoMachine's concrete states are not needed; patching this
//   single evaluator method covers all code paths that process footage.
//
// NOTE: All patches use AccessTools string-based type/method resolution so they
// gracefully no-op (log a warning) if the game renames a method, rather than
// crashing on startup.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ContentWarningArchipelago.Data;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // PATCH 1 — Shop item purchased (master-client side, after CanAfford check)
    // =========================================================================
    [HarmonyPatch]
    public static class ShopBuyPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("ShopHandler");
            if (type == null)
            {
                Plugin.Logger.LogWarning("[ShopBuyPatch] Could not find type 'ShopHandler'. Patch skipped.");
                return null;
            }

            // RPCA_SpawnDrone(byte[] itemIDs) — private PunRPC called only when
            // CanAfford succeeds on the master client.
            var method = AccessTools.Method(type, "RPCA_SpawnDrone");
            if (method != null)
            {
                Plugin.Logger.LogInfo($"[ShopBuyPatch] Patching {type.Name}.{method.Name}");
                return method;
            }

            Plugin.Logger.LogWarning("[ShopBuyPatch] Could not find 'RPCA_SpawnDrone' on ShopHandler. Patch skipped.");
            return null;
        }

        /// <summary>
        /// Postfix fires on EVERY client (RpcTarget.All broadcast).
        /// We guard with IsMasterClient so only the host sends the AP check once.
        /// <paramref name="itemIDs"/> is the exact set of purchased item IDs.
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(object __instance, byte[] itemIDs)
        {
            if (!Plugin.connection.connected) return;

            // Only the master client sends checks — prevents duplicate sends.
            if (!PhotonNetwork.IsMasterClient) return;

            try
            {
                foreach (byte itemId in itemIDs)
                {
                    string? displayName = TryGetItemDisplayName(itemId);
                    if (string.IsNullOrEmpty(displayName))
                    {
                        Plugin.Logger.LogDebug($"[ShopBuyPatch] Could not resolve display name for item ID {itemId}.");
                        continue;
                    }

                    string locationName = "Bought " + displayName;
                    long locId = LocationData.GetId(locationName);
                    if (locId < 0)
                    {
                        Plugin.Logger.LogDebug($"[ShopBuyPatch] '{locationName}' is not an AP location (non-AP item).");
                        continue;
                    }

                    Plugin.Logger.LogInfo($"[ShopBuyPatch] Purchase confirmed → sending check: {locationName}");
                    Plugin.SendCheck(locId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ShopBuyPatch] Exception in postfix: {ex}");
            }
        }

        /// <summary>
        /// Resolves the display name of a shop item from its byte ID using
        /// <c>ItemDatabase.TryGetItemFromID</c> via reflection.
        /// Returns null if the item could not be found.
        /// </summary>
        private static string? TryGetItemDisplayName(byte itemId)
        {
            // ItemDatabase.TryGetItemFromID(byte id, out Item item) is a static method.
            var dbType = AccessTools.TypeByName("ItemDatabase");
            if (dbType == null) return null;

            var tryGet = AccessTools.Method(dbType, "TryGetItemFromID");
            if (tryGet == null) return null;

            var args = new object[] { itemId, null! };
            bool found = (bool)(tryGet.Invoke(null, args) ?? false);
            if (!found || args[1] == null) return null;

            object item = args[1];

            // Prefer "displayName" field (matches location table naming).
            var displayField = AccessTools.Field(item.GetType(), "displayName");
            if (displayField != null)
            {
                string? display = displayField.GetValue(item)?.ToString();
                if (!string.IsNullOrWhiteSpace(display)) return display;
            }

            // Fallback to Unity Object.name.
            if (item is UnityEngine.Object unityObj && !string.IsNullOrWhiteSpace(unityObj.name))
                return unityObj.name;

            return null;
        }
    }

    // =========================================================================
    // PATCH 2 — Physical item picked up from the world
    // Foundation for future pickup-based AP location checks.
    // =========================================================================
    [HarmonyPatch]
    public static class PickupPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("Pickup");
            if (type == null)
            {
                Plugin.Logger.LogWarning("[PickupPatch] Could not find type 'Pickup'. Patch skipped.");
                return null;
            }

            var method = AccessTools.Method(type, "RPC_RequestPickup");
            if (method == null)
            {
                Plugin.Logger.LogWarning("[PickupPatch] Could not find 'RPC_RequestPickup' on Pickup. Patch skipped.");
                return null;
            }

            Plugin.Logger.LogInfo($"[PickupPatch] Patching {type.Name}.{method.Name}");
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (!Plugin.connection.connected) return;

            try
            {
                string? itemName = TryGetPickupName(__instance);
                Plugin.Logger.LogDebug($"[PickupPatch] Item picked up: {itemName ?? "(unknown)"}");
                // Future: map specific pickup names to AP location IDs here.
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[PickupPatch] Exception in postfix: {ex}");
            }
        }

        private static string? TryGetPickupName(object instance)
        {
            Type t = instance.GetType();

            var itemInstanceField = AccessTools.Field(t, "itemInstance");
            if (itemInstanceField != null)
            {
                var itemInstance = itemInstanceField.GetValue(instance);
                if (itemInstance != null)
                {
                    var itemField = AccessTools.Field(itemInstance.GetType(), "item");
                    if (itemField != null)
                    {
                        var item = itemField.GetValue(itemInstance);
                        if (item != null)
                        {
                            var displayNameField = AccessTools.Field(item.GetType(), "displayName");
                            if (displayNameField != null)
                            {
                                var displayName = displayNameField.GetValue(item)?.ToString();
                                if (!string.IsNullOrEmpty(displayName)) return displayName;
                            }
                            if (item is UnityEngine.Object unityItem) return unityItem.name;
                        }
                    }
                }
            }

            if (instance is Component comp) return comp.gameObject.name;
            return null;
        }
    }

    // =========================================================================
    // PATCH 3 — ContentEvaluator.EvaluateRecording
    //
    // Handles both:
    //   • Extraction location checks  ("Any Extraction", "Extracted Footage on Day N")
    //   • Filming location checks     ("Filmed Slurper", "Filmed Zombe", etc.)
    //
    // Confirmed signature (VideoDebugPage.cs):
    //   bool ContentEvaluator.EvaluateRecording(CameraRecording recording, out ContentBuffer buffer)
    //
    // Called by ExtractVideoMachine's state machine when the extraction terminal
    // finishes processing a disk.
    // =========================================================================
    [HarmonyPatch]
    public static class ContentEvaluatorPatch
    {
        static MethodBase? TargetMethod()
        {
            // Try ContentEvaluator first (confirmed in VideoDebugPage.cs reference).
            foreach (string typeName in new[] { "ContentEvaluator" })
            {
                var type = AccessTools.TypeByName(typeName);
                if (type == null) continue;

                var method = AccessTools.Method(type, "EvaluateRecording");
                if (method != null)
                {
                    Plugin.Logger.LogInfo($"[ContentEvaluatorPatch] Patching {type.Name}.{method.Name}");
                    return method;
                }
            }

            Plugin.Logger.LogWarning(
                "[ContentEvaluatorPatch] Could not find ContentEvaluator.EvaluateRecording. " +
                "Extraction and filming checks will not fire. " +
                "Verify the class name in Assembly-CSharp.");
            return null;
        }

        /// <summary>
        /// Postfix fires after <c>ContentEvaluator.EvaluateRecording</c> returns.
        /// <paramref name="__result"/> is <c>true</c> when evaluation succeeded.
        /// <paramref name="buffer"/> is the out-parameter ContentBuffer containing
        /// scored content frames.
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(bool __result, object recording, ref object buffer)
        {
            if (!__result) return;                    // evaluation failed / no content
            if (!Plugin.connection.connected) return;

            try
            {
                // ---- a) Any Extraction -----------------------------------------------
                long anyExtrId = LocationData.GetId(LocationNames.AnyExtraction);
                if (anyExtrId >= 0)
                {
                    Plugin.Logger.LogInfo("[ContentEvaluatorPatch] Sending check: Any Extraction");
                    Plugin.SendCheck(anyExtrId);
                }

                // ---- b) Extracted Footage on Day N ------------------------------------
                int day = TryGetCurrentDay();
                if (day > 0 && day <= 15)
                {
                    string dayLocName = LocationNames.ExtractedFootagePrefix + day;
                    long dayLocId = LocationData.GetId(dayLocName);
                    if (dayLocId >= 0)
                    {
                        Plugin.Logger.LogInfo($"[ContentEvaluatorPatch] Day {day} extraction → sending check: {dayLocName}");
                        Plugin.SendCheck(dayLocId);
                    }
                }

                // ---- c) Filmed <Monster/Artifact> ------------------------------------
                if (buffer != null)
                {
                    FireFilmingChecks(buffer);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ContentEvaluatorPatch] Exception in postfix: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // Filming detection
        // ------------------------------------------------------------------

        /// <summary>
        /// Iterates the ContentBuffer's internal frames/events to find filmed
        /// entities and fire the corresponding "Filmed X" AP location checks.
        /// Uses reflection since ContentBuffer internals are not in the reference.
        /// </summary>
        private static void FireFilmingChecks(object buffer)
        {
            // Track which location names we've already fired this evaluation
            // to avoid duplicate checks if the same entity appears in many frames.
            var fired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Strategy: walk every field/property on the ContentBuffer looking
            // for a collection of frames or events, then inspect each item.
            Type bufferType = buffer.GetType();

            foreach (FieldInfo field in bufferType.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? fieldValue = field.GetValue(buffer);
                if (fieldValue == null) continue;

                // If the field is a collection, iterate it.
                if (fieldValue is IEnumerable enumerable && !(fieldValue is string))
                {
                    foreach (object? item in enumerable)
                    {
                        if (item == null) continue;
                        TryFireFromContentItem(item, fired);
                    }
                }
                else
                {
                    TryFireFromContentItem(fieldValue, fired);
                }
            }
        }

        /// <summary>
        /// Tries to extract an entity type identifier from a single content item
        /// (ContentEventFrame, ContentEvent, or similar) and fire the AP check.
        /// </summary>
        private static void TryFireFromContentItem(object item, HashSet<string> fired)
        {
            Type t = item.GetType();

            // --- Strategy 1: look for an IEnumerable of child events ---------------
            foreach (FieldInfo f in t.GetFields(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                object? val = f.GetValue(item);
                if (val is IEnumerable innerEnum && !(val is string))
                {
                    foreach (object? child in innerEnum)
                    {
                        if (child != null)
                            TryFireFromLeafEvent(child, fired);
                    }
                }
            }

            // --- Strategy 2: treat this item itself as a leaf event ----------------
            TryFireFromLeafEvent(item, fired);
        }

        /// <summary>
        /// Tries to extract a type identifier from a leaf content event object
        /// and fire the corresponding "Filmed X" check.
        /// Probes common field/property names for entity type information.
        /// </summary>
        private static void TryFireFromLeafEvent(object ev, HashSet<string> fired)
        {
            Type t = ev.GetType();

            // ---- Try to get a string type name ----
            string? typeName = null;

            // Probe fields: "type", "contentType", "entityType", "creatureType",
            //               "name", "entityName", "contentTypeName"
            foreach (string candidate in new[]
            {
                "type", "contentType", "entityType", "creatureType",
                "entityName", "contentTypeName", "typeName"
            })
            {
                var field = AccessTools.Field(t, candidate);
                if (field != null)
                {
                    object? raw = field.GetValue(ev);
                    if (raw != null)
                    {
                        typeName = raw.ToString();
                        break;
                    }
                }
                var prop = AccessTools.Property(t, candidate);
                if (prop != null)
                {
                    object? raw = prop.GetValue(ev);
                    if (raw != null)
                    {
                        typeName = raw.ToString();
                        break;
                    }
                }
            }

            // Also try to get a numeric ID
            int typeId = -1;
            foreach (string candidate in new[] { "id", "typeId", "entityId", "contentId", "creatureId" })
            {
                var field = AccessTools.Field(t, candidate);
                if (field != null && field.GetValue(ev) is int rawId)
                {
                    typeId = rawId;
                    break;
                }
                var prop = AccessTools.Property(t, candidate);
                if (prop != null && prop.GetValue(ev) is int rawPropId)
                {
                    typeId = rawPropId;
                    break;
                }
            }

            // ---- Resolve to AP location name ----
            string? locationName = null;

            if (!string.IsNullOrEmpty(typeName))
            {
                locationName = MonsterFilmingData.TryGetLocationByTypeName(typeName!);
                if (locationName == null)
                {
                    // Log for discovery — user can add to MonsterFilmingData.
                    Plugin.Logger.LogDebug(
                        $"[ContentEvaluatorPatch] Unrecognised entity type string: '{typeName}' " +
                        $"(class: {t.Name}). Add to MonsterFilmingData if this is a monster/artifact.");
                }
            }

            if (locationName == null && typeId >= 0)
            {
                locationName = MonsterFilmingData.TryGetLocationById(typeId);
                if (locationName == null)
                {
                    Plugin.Logger.LogDebug(
                        $"[ContentEvaluatorPatch] Unrecognised entity type ID: {typeId} " +
                        $"(class: {t.Name}). Add to MonsterFilmingData.EntityIdToLocation.");
                }
            }

            if (string.IsNullOrEmpty(locationName)) return;

            // ---- Fire the check (once per location per evaluation) ----
            if (fired.Contains(locationName)) return;
            fired.Add(locationName);

            long locId = LocationData.GetId(locationName);
            if (locId < 0)
            {
                Plugin.Logger.LogDebug($"[ContentEvaluatorPatch] '{locationName}' not in AP location table.");
                return;
            }

            Plugin.Logger.LogInfo($"[ContentEvaluatorPatch] Filming check → {locationName}");
            Plugin.SendCheck(locId);
        }

        // ------------------------------------------------------------------
        // Day resolution
        // ------------------------------------------------------------------

        /// <summary>
        /// Reads the current in-game day number from SurfaceNetworkHandler
        /// or any other known game manager.  Returns 0 if unresolvable.
        /// </summary>
        private static int TryGetCurrentDay()
        {
            // Try static fields / singleton instances on known manager types.
            foreach (string typeName in new[]
            {
                "SurfaceNetworkHandler", "GameDirector", "GameManager"
            })
            {
                var mgr = AccessTools.TypeByName(typeName);
                if (mgr == null) continue;

                foreach (string fname in new[] { "day", "currentDay", "dayNumber", "m_day", "m_currentDay" })
                {
                    // Direct static field.
                    var sf = AccessTools.Field(mgr, fname);
                    if (sf?.IsStatic == true)
                    {
                        var val = sf.GetValue(null);
                        if (val is int i && i > 0) return i;
                    }

                    // Instance via static "instance" / "Instance" field.
                    foreach (string instName in new[] { "instance", "Instance" })
                    {
                        var instField = AccessTools.Field(mgr, instName);
                        if (instField?.IsStatic != true) continue;

                        var singleton = instField.GetValue(null);
                        if (singleton == null) continue;

                        var dayField = AccessTools.Field(singleton.GetType(), fname);
                        if (dayField != null)
                        {
                            var val = dayField.GetValue(singleton);
                            if (val is int i2 && i2 > 0) return i2;
                        }
                    }
                }
            }

            return 0;
        }
    }
}
