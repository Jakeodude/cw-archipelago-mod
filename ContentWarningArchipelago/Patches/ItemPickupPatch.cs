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
        /// Iterates the ContentBuffer's <c>buffer</c> list (List&lt;BufferedContent&gt;),
        /// navigates the known field chain
        ///   BufferedContent.frame → ContentEventFrame.contentEvent → ContentEvent subclass,
        /// and fires "Filmed X" AP location checks for each distinct entity type found.
        ///
        /// Field names confirmed from game source (MonsterEventCombiner.cs uses the same path):
        ///   ContentBuffer.buffer          — List&lt;BufferedContent&gt;
        ///   BufferedContent.frame         — ContentEventFrame
        ///   ContentEventFrame.contentEvent — ContentEvent (MonsterContentEvent subclass at runtime)
        ///   contentEvent.GetType().Name   — entity class name (matches MonsterFilmingData keys)
        ///   contentEvent.GetID()          — ushort type ID (numeric fallback)
        /// </summary>
        private static void FireFilmingChecks(object buffer)
        {
            var fired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Type bufferType = buffer.GetType();

            // ContentBuffer.buffer → List<BufferedContent>
            var bufferListField = AccessTools.Field(bufferType, "buffer");
            if (bufferListField == null)
            {
                Plugin.Logger.LogWarning("[ContentEvaluatorPatch] Could not find 'buffer' field on ContentBuffer. Filming checks skipped.");
                return;
            }

            var bufferList = bufferListField.GetValue(buffer) as IEnumerable;
            if (bufferList == null) return;

            foreach (object? bufferedContent in bufferList)
            {
                if (bufferedContent == null) continue;

                // BufferedContent.frame → ContentEventFrame
                var frameField = AccessTools.Field(bufferedContent.GetType(), "frame");
                if (frameField == null) continue;
                var frame = frameField.GetValue(bufferedContent);
                if (frame == null) continue;

                // ContentEventFrame.contentEvent → ContentEvent (MonsterContentEvent subclass)
                var contentEventField = AccessTools.Field(frame.GetType(), "contentEvent");
                if (contentEventField == null) continue;
                var contentEvent = contentEventField.GetValue(frame);
                if (contentEvent == null) continue;

                // Primary identifier: runtime class name (e.g. "Slurper", "Zombe")
                string rawName = contentEvent.GetType().Name;

                // Numeric fallback: contentEvent.GetID() returns ushort
                int typeId = -1;
                var getIdMethod = contentEvent.GetType().GetMethod(
                    "GetID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getIdMethod != null)
                {
                    var raw = getIdMethod.Invoke(contentEvent, null);
                    if (raw != null) typeId = Convert.ToInt32(raw);
                }

                TryFireEntityCheck(rawName, typeId, fired);
            }
        }

        /// <summary>
        /// Resolves a filmed entity to an AP location name and sends the check.
        /// Tries the raw class name first, then strips a "ContentEvent" suffix as a
        /// fallback (handles both "Slurper" and "SlurperContentEvent" style names),
        /// then falls back to the numeric ID.
        /// </summary>
        private static void TryFireEntityCheck(string rawName, int typeId, HashSet<string> fired)
        {
            // 1. Try the class name directly (e.g. "Slurper")
            string? locationName = MonsterFilmingData.TryGetLocationByTypeName(rawName);

            // 2. Try stripping "ContentEvent" suffix (e.g. "SlurperContentEvent" → "Slurper")
            if (locationName == null && rawName.EndsWith("ContentEvent", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = rawName.Substring(0, rawName.Length - "ContentEvent".Length);
                locationName = MonsterFilmingData.TryGetLocationByTypeName(stripped);
            }

            // 3. Numeric ID fallback
            if (locationName == null && typeId >= 0)
                locationName = MonsterFilmingData.TryGetLocationById(typeId);

            if (locationName == null)
            {
                Plugin.Logger.LogDebug(
                    $"[ContentEvaluatorPatch] Unknown entity: class='{rawName}', ID={typeId}. " +
                    $"Add to MonsterFilmingData if this is a monster/artifact.");
                return;
            }

            // Fire once per distinct location per evaluation pass
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
        /// Reads the current in-game day number.  Returns 0 if unresolvable.
        ///
        /// SurfaceNetworkHandler has NO direct day/currentDay field.
        /// The day is stored in RoomStats.CurrentDay (confirmed from SurfaceNetworkHandler.cs):
        ///   public static RoomStatsHolder RoomStats { get; private set; }
        ///   RoomStats.CurrentDay  (property on RoomStatsHolder)
        ///
        /// The game also exposes GameAPI.CurrentDay which ContentBuffer.GenerateComments()
        /// uses directly — that is the simplest and most reliable path.
        /// </summary>
        private static int TryGetCurrentDay()
        {
            // Strategy 1: GameAPI.CurrentDay (static property)
            // ContentBuffer.GenerateComments() uses this exact call internally.
            var gameApiType = AccessTools.TypeByName("GameAPI");
            if (gameApiType != null)
            {
                var prop = AccessTools.Property(gameApiType, "CurrentDay");
                if (prop != null)
                {
                    var val = prop.GetValue(null);
                    if (val is int d && d > 0) return d;
                }
            }

            // Strategy 2: SurfaceNetworkHandler.RoomStats (static property) → .CurrentDay
            // RoomStats is a static property, not a field; CurrentDay is a property on RoomStatsHolder.
            var snhType = AccessTools.TypeByName("SurfaceNetworkHandler");
            if (snhType != null)
            {
                var roomStatsProp = AccessTools.Property(snhType, "RoomStats");
                if (roomStatsProp != null)
                {
                    var roomStats = roomStatsProp.GetValue(null);
                    if (roomStats != null)
                    {
                        var currentDayProp = AccessTools.Property(roomStats.GetType(), "CurrentDay");
                        if (currentDayProp != null)
                        {
                            var val = currentDayProp.GetValue(roomStats);
                            if (val is int d && d > 0) return d;
                        }
                    }
                }
            }

            return 0;
        }
    }
}
