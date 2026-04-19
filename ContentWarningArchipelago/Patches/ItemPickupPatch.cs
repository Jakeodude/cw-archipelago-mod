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
//        the ContentBuffer, looked up in FilmingLocationData.
//
//   CONTENT EVENT DISPATCH LOGIC:
//     ContentEventIDMapper.GetContentEvent(id) returns:
//       • IDs 1000–1045 → specific MonsterContentEvent subclasses (SlurperContentEvent, etc.)
//       • Other IDs     → ArtifactContentEvent { content = PropContent }  (isArtifact == true)
//                         PropContentEvent     { content = PropContent }  (isArtifact == false)
//
//     FireFilmingChecks handles all three event types:
//       1. Reads contentEvent.GetID() as ushort for direct monster ID matching.
//       2. Detects ArtifactContentEvent by class name; drills into content.displayName
//          via reflection to identify which artifact was filmed.
//       3. Falls back to class-name string matching for any uncovered event type.
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
    //   • Filming location checks     ("Filmed Slurper", "Filmed Skull", etc.)
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
        /// navigates the known field chain:
        ///   BufferedContent.frame → ContentEventFrame.contentEvent → ContentEvent subclass
        ///
        /// For each event it:
        ///   1. Reads contentEvent.GetID() as a ushort for direct monster ID matching.
        ///   2. Detects ArtifactContentEvent by class name; uses reflection to access
        ///      the nested PropContent object and read its displayName field to
        ///      identify the specific artifact filmed.
        ///   3. Calls TryFireEntityCheck with all gathered context to resolve and
        ///      send the AP location check with 3-level priority fallback.
        ///
        /// Field names confirmed from game source:
        ///   ContentBuffer.buffer           — List&lt;BufferedContent&gt;
        ///   BufferedContent.frame          — ContentEventFrame (struct)
        ///   ContentEventFrame.contentEvent — ContentEvent (runtime subclass)
        ///   ArtifactContentEvent.content   — PropContent (ScriptableObject)
        ///   PropContent.displayName        — string artifact display name
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

                // BufferedContent.frame → ContentEventFrame (struct)
                var frameField = AccessTools.Field(bufferedContent.GetType(), "frame");
                if (frameField == null) continue;
                var frame = frameField.GetValue(bufferedContent);
                if (frame == null) continue;

                // ContentEventFrame.contentEvent → ContentEvent subclass
                var contentEventField = AccessTools.Field(frame.GetType(), "contentEvent");
                if (contentEventField == null) continue;
                var contentEvent = contentEventField.GetValue(frame);
                if (contentEvent == null) continue;

                // --- Resolve ushort event ID via contentEvent.GetID() ---
                ushort eventId = 0;
                var getIdMethod = contentEvent.GetType().GetMethod(
                    "GetID", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getIdMethod != null)
                {
                    var rawId = getIdMethod.Invoke(contentEvent, null);
                    if (rawId != null)
                    {
                        try { eventId = Convert.ToUInt16(rawId); }
                        catch { /* ignore overflow; eventId stays 0 */ }
                    }
                }

                // --- Detect ArtifactContentEvent: extract PropContent.displayName ---
                // ArtifactContentEvent is generated by ContentEventIDMapper when the ID
                // resolves to a PropContent with isArtifact == true.  The class name is
                // always "ArtifactContentEvent" regardless of which artifact it is, so
                // we must read the nested 'content' field to find the artifact identity.
                string? artifactDisplayName = null;
                string rawName = contentEvent.GetType().Name;

                if (rawName.Equals("ArtifactContentEvent", StringComparison.Ordinal))
                {
                    var contentField = AccessTools.Field(contentEvent.GetType(), "content");
                    if (contentField != null)
                    {
                        var propContent = contentField.GetValue(contentEvent);
                        if (propContent != null)
                        {
                            // Try PropContent.displayName first (matches EntityTypeToLocation keys)
                            var displayNameField = AccessTools.Field(propContent.GetType(), "displayName");
                            if (displayNameField != null)
                                artifactDisplayName = displayNameField.GetValue(propContent)?.ToString();

                            // Fallback: Unity Object.name (ScriptableObject asset name)
                            if (string.IsNullOrWhiteSpace(artifactDisplayName) && propContent is UnityEngine.Object uo)
                                artifactDisplayName = uo.name;

                            Plugin.Logger.LogDebug(
                                $"[ContentEvaluatorPatch] ArtifactContentEvent detected: " +
                                $"displayName='{artifactDisplayName}', ID={eventId}");
                        }
                    }
                }

                TryFireEntityCheck(rawName, eventId, artifactDisplayName, fired);
            }
        }

        /// <summary>
        /// Resolves a filmed entity to an AP location name and sends the check.
        ///
        /// Priority order:
        ///   1. <b>Specific ID match</b>  — looks up <paramref name="id"/> directly in
        ///      <see cref="FilmingLocationData.EntityIdToLocation"/> (covers all hardcoded
        ///      monster event IDs 1000–1045 from ContentEventIDMapper).
        ///   2. <b>Artifact name match</b> — if <paramref name="artifactDisplayName"/> is
        ///      non-null (set when the event is an ArtifactContentEvent), looks up the
        ///      display name in <see cref="FilmingLocationData.EntityTypeToLocation"/>.
        ///   3. <b>Class name match</b>   — looks up the raw runtime class name, then
        ///      retries after stripping a trailing "ContentEvent" suffix, to handle both
        ///      "Slurper" and "SlurperContentEvent" style names from any event not
        ///      already resolved above.
        /// </summary>
        private static void TryFireEntityCheck(
            string rawName,
            ushort id,
            string? artifactDisplayName,
            HashSet<string> fired)
        {
            string? locationName = null;

            // ── Priority 1: Direct ushort ID match ──────────────────────────────────
            // Covers all hardcoded monster IDs from ContentEventIDMapper (1000–1045).
            if (id != 0)
                locationName = FilmingLocationData.TryGetLocationById(id);

            // ── Priority 2: Artifact display-name match ──────────────────────────────
            // Only populated when FireFilmingChecks detected an ArtifactContentEvent.
            // e.g. PropContent.displayName "Ribcage" → "Filmed Ribcage"
            if (locationName == null && !string.IsNullOrEmpty(artifactDisplayName))
                locationName = FilmingLocationData.TryGetLocationByTypeName(artifactDisplayName);

            // ── Priority 3: Runtime class-name match (legacy / catch-all) ────────────
            if (locationName == null)
                locationName = FilmingLocationData.TryGetLocationByTypeName(rawName);

            // Strip trailing "ContentEvent" suffix and retry
            // (handles "SlurperContentEvent" → "Slurper", etc.)
            if (locationName == null && rawName.EndsWith("ContentEvent", StringComparison.OrdinalIgnoreCase))
            {
                string stripped = rawName.Substring(0, rawName.Length - "ContentEvent".Length);
                locationName = FilmingLocationData.TryGetLocationByTypeName(stripped);
            }

            if (locationName == null)
            {
                Plugin.Logger.LogDebug(
                    $"[ContentEvaluatorPatch] No AP location for: class='{rawName}', ID={id}, " +
                    $"artifactName='{artifactDisplayName ?? "n/a"}'. " +
                    $"Add to FilmingLocationData if this is a new monster/artifact.");
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
