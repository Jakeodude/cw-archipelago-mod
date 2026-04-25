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
//   TWO Postfix methods run after evaluation is complete:
//
//   a) Postfix — "Any Extraction" / "Extracted Footage on Day N"
//      Fires the day-keyed extraction location checks.
//      Guarded by IsMasterClient — only the host sends these checks and
//      broadcasts RPC notifications to all clients.
//
//   b) FilmingPostfix — Filming detection
//      Score multiplier (Progressive Views) runs on ALL clients.
//      AP check firing is guarded to master client only; non-master clients
//      receive popups via RPCA_BroadcastAPCheckNotification.
//
//   NOTE ON PREFIX VS POSTFIX:
//   The task requests a "Prefix" here, but since `buffer` is an `out` parameter
//   it is null before the method body runs.  Only a Postfix can access the
//   populated ContentBuffer.  FilmingPostfix is therefore a [HarmonyPostfix].
//
//   Field chain confirmed from ContentBuffer.cs game reference:
//     ContentBuffer.buffer           — List<BufferedContent>  (public field)
//     BufferedContent.frame          — ContentEventFrame      (public field)
//     ContentEventFrame.contentEvent — ContentEvent subclass  (public field)
//     ArtifactContentEvent.artifact  — PropContent (primary field; specific name e.g. 'Ribcage')
//     ArtifactContentEvent.content   — PropContent (fallback;  generic  name e.g. 'Bones')
//     PropContent is a ScriptableObject; use Object.name for the asset name.
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
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using ContentWarningArchipelago.UI;
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
        // ── Explicit override map ────────────────────────────────────────────
        // Maps a game-internal name (Unity asset name OR Item.displayName) to the
        // correct AP location name for items whose auto-generated
        // "Bought " + displayName does NOT match the location table.
        //
        // WHY NEEDED:
        //   Some emote Items have an empty/null displayName and rely entirely on
        //   the localization system (ShopItem.UpdateLocalizedName → LocalizationKeys).
        //   When displayName is missing, TryGetItemDisplayName falls back to
        //   UnityEngine.Object.name (the Unity asset name), which is typically
        //   camelCase or spaceless (e.g. "AncientGestures3", "Dance101").
        //   The auto-computed "Bought AncientGestures3" never matches the
        //   location-table entry "Bought Ancient Gestures 3", so the check is lost.
        //
        // HOW IT IS USED (in Postfix):
        //   1. Check override by assetName  (covers null-displayName items)
        //   2. Check override by displayName (covers items with wrong displayName)
        //   3. Auto-compute "Bought " + displayName  (normal path; most items)
        //   4. Auto-compute "Bought " + assetName    (last-resort fallback)
        //
        // Keys are case-insensitive.  Both the spaceless Unity asset name AND the
        // human-readable display name variant are registered so the lookup works
        // regardless of what the game version actually stores in each field.
        // ─────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, string> _shopNameOverrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Emotes: numbered / multi-word names that may differ internally ──
            { "AncientGestures3",   "Bought Ancient Gestures 3" },
            { "Ancient Gestures 3", "Bought Ancient Gestures 3" },
            { "AncientGestures2",   "Bought Ancient Gestures 2" },
            { "Ancient Gestures 2", "Bought Ancient Gestures 2" },
            { "AncientGestures1",   "Bought Ancient Gestures 1" },
            { "Ancient Gestures 1", "Bought Ancient Gestures 1" },
            // "Backflip" may appear as "Backflip", "BackFlip", or a suffixed asset name
            { "Backflip",           "Bought Backflip" },
            { "BackFlip",           "Bought Backflip" },
            { "Backflip_Emote",     "Bought Backflip" },
            { "Dance101",           "Bought Dance 101" },
            { "Dance 101",          "Bought Dance 101" },
            { "Dance102",           "Bought Dance 102" },
            { "Dance 102",          "Bought Dance 102" },
            { "Dance103",           "Bought Dance 103" },
            { "Dance 103",          "Bought Dance 103" },
            { "Workout1",           "Bought Workout 1" },
            { "Workout 1",          "Bought Workout 1" },
            { "Workout2",           "Bought Workout 2" },
            { "Workout 2",          "Bought Workout 2" },
            { "Thumbnail1",         "Bought Thumbnail 1" },
            { "Thumbnail 1",        "Bought Thumbnail 1" },
            { "Thumbnail2",         "Bought Thumbnail 2" },
            { "Thumbnail 2",        "Bought Thumbnail 2" },
            { "Gymnastics",         "Bought Gymnastics" },
            { "Caring",             "Bought Caring" },
            { "Yoga",               "Bought Yoga" },
            // "Party Popper" works via displayName already, but add the spaceless
            // form in case a game patch strips the displayName.
            { "PartyPopper",        "Bought Party Popper" },
        };

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
        ///
        /// Location name resolution order for each purchased item:
        ///   1. Override by assetName  — catches emotes with null/empty displayName whose
        ///      Unity asset name differs from the AP location name (e.g. "AncientGestures3").
        ///   2. Override by displayName — catches items whose displayName doesn't auto-match.
        ///   3. Auto "Bought " + displayName — the normal path used by most shop items.
        ///   4. Auto "Bought " + assetName  — last-resort fallback.
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
                    TryGetItemDisplayName(itemId, out string? displayName, out string? assetName);

                    // ── Step 1: explicit override by asset name ────────────────────────
                    string? locationName = null;
                    if (!string.IsNullOrEmpty(assetName) &&
                        _shopNameOverrides.TryGetValue(assetName, out string? ov1))
                        locationName = ov1;

                    // ── Step 2: explicit override by display name ──────────────────────
                    if (locationName == null &&
                        !string.IsNullOrEmpty(displayName) &&
                        _shopNameOverrides.TryGetValue(displayName, out string? ov2))
                        locationName = ov2;

                    // ── Step 3: auto-compute "Bought " + displayName ───────────────────
                    if (locationName == null && !string.IsNullOrEmpty(displayName))
                        locationName = "Bought " + displayName;

                    // ── Step 4: auto-compute "Bought " + assetName (last resort) ───────
                    if (locationName == null && !string.IsNullOrEmpty(assetName))
                        locationName = "Bought " + assetName;

                    if (string.IsNullOrEmpty(locationName))
                    {
                        Plugin.Logger.LogDebug(
                            $"[ShopBuyPatch] Could not resolve any name for item ID {itemId}.");
                        continue;
                    }

                    long locId = LocationData.GetId(locationName);
                    if (locId < 0)
                    {
                        Plugin.Logger.LogDebug(
                            $"[ShopBuyPatch] '{locationName}' is not an AP location (non-AP item).");
                        continue;
                    }

                    Plugin.Logger.LogInfo(
                        $"[ShopBuyPatch] Purchase confirmed → sending check: {locationName}");
                    Plugin.SendCheck(locId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ShopBuyPatch] Exception in postfix: {ex}");
            }
        }

        /// <summary>
        /// Resolves the display name AND Unity asset name of a shop item from its byte ID
        /// using <c>ItemDatabase.TryGetItemFromID</c> via reflection.
        ///
        /// <para>
        /// <paramref name="displayName"/> is the value of <c>Item.displayName</c> (the
        /// designer-set English string, e.g. "Modern Flashlight").  It is <c>null</c>
        /// when the field is empty/missing — some emote items have no displayName and
        /// rely entirely on the localization system.
        /// </para>
        /// <para>
        /// <paramref name="assetName"/> is <c>UnityEngine.Object.name</c> — the Unity
        /// asset name (e.g. "AncientGestures3", "Backflip").  Always available when the
        /// item was found in the database.
        /// </para>
        /// Both outputs are <c>null</c> when the item ID is not in the database.
        /// </summary>
        private static void TryGetItemDisplayName(
            byte itemId,
            out string? displayName,
            out string? assetName)
        {
            displayName = null;
            assetName   = null;

            // ItemDatabase.TryGetItemFromID(byte id, out Item item) is a static method.
            var dbType = AccessTools.TypeByName("ItemDatabase");
            if (dbType == null) return;

            var tryGet = AccessTools.Method(dbType, "TryGetItemFromID");
            if (tryGet == null) return;

            var args = new object[] { itemId, null! };
            bool found = (bool)(tryGet.Invoke(null, args) ?? false);
            if (!found || args[1] == null) return;

            object item = args[1];

            // Capture the Unity asset name first (always present on a UnityEngine.Object).
            if (item is UnityEngine.Object unityObj && !string.IsNullOrWhiteSpace(unityObj.name))
                assetName = unityObj.name;

            // Prefer "displayName" field (matches location table naming for most items).
            var displayField = AccessTools.Field(item.GetType(), "displayName");
            if (displayField != null)
            {
                string? display = displayField.GetValue(item)?.ToString();
                if (!string.IsNullOrWhiteSpace(display))
                    displayName = display;
            }
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
    // Handles both extraction location checks AND filming detection:
    //   • "Any Extraction"
    //   • "Extracted Footage on Day N"
    //   • "Filmed <Monster/Artifact>" (moved here from ContentBufferGenerateCommentsPatch)
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

        // ------------------------------------------------------------------
        // Postfix A — Extraction location checks
        // ------------------------------------------------------------------

        /// <summary>
        /// Postfix fires after <c>ContentEvaluator.EvaluateRecording</c> returns.
        /// <paramref name="__result"/> is <c>true</c> when evaluation succeeded.
        /// Fires extraction location checks only.
        ///
        /// Guarded by <c>IsMasterClient</c>: only the host sends AP checks and
        /// broadcasts notifications to all clients via
        /// <see cref="BroadcastAPCheckNotification"/>.  Non-master clients receive
        /// the <c>RPCA_BroadcastAPCheckNotification</c> RPC and display the popup
        /// there instead.
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(bool __result)
        {
            if (!__result) return;                     // evaluation failed / no content
            if (!Plugin.connection.connected) return;
            if (!PhotonNetwork.IsMasterClient) return; // only host sends checks + notifications

            try
            {
                // ---- a) Any Extraction -----------------------------------------------
                long anyExtrId = LocationData.GetId(LocationNames.AnyExtraction);
                if (anyExtrId >= 0)
                {
                    Plugin.Logger.LogInfo("[ContentEvaluatorPatch] Sending check: Any Extraction");
                    Plugin.SendCheck(anyExtrId);
                    BroadcastAPCheckNotification(LocationNames.AnyExtraction);
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
                        BroadcastAPCheckNotification(dayLocName);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ContentEvaluatorPatch] Exception in extraction postfix: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // Postfix B — Filming detection
        //
        // NOTE: The task specifies a "Prefix" here, but since `buffer` is an
        // `out` parameter it is null before the method body runs.  Only a
        // Postfix can access the populated ContentBuffer.
        //
        // Harmony injects the `out ContentBuffer buffer` value by matching the
        // parameter name "buffer" to the original method's parameter name.
        // We type it as `object` to avoid a hard compile-time dependency on
        // ContentBuffer (consistent with the rest of this patch file).
        // ------------------------------------------------------------------

        /// <summary>
        /// Second Postfix — applies the Progressive Views score multiplier on
        /// ALL clients, then fires filming AP checks on the master client only.
        ///
        /// Non-master clients (e.g. 'Reggi') receive filming check popups via
        /// <c>RPCA_BroadcastAPCheckNotification</c> dispatched from the master.
        /// </summary>
        [HarmonyPostfix]
        static void FilmingPostfix(bool __result, object buffer)
        {
            if (!__result) return;
            if (!Plugin.connection.connected) return;
            if (buffer == null) return;

            try
            {
                // ── Progressive Views: multiply every BufferedContent.score ───────────
                // Multiplying the scores here (before GenerateComments converts them via
                // BigNumbers.GetScoreToViews) boosts extracted-footage view counts only.
                // The quota display (UI_Views) reads SurfaceNetworkHandler.RoomStats
                // directly and is completely unaffected by changes to buffer scores.
                // This runs on ALL clients because the buffer is local to each client.
                int viewLevel = APSave.saveData.viewsMultiplierLevel;
                if (viewLevel > 0)
                {
                    float viewMult = 1.0f + viewLevel * 0.1f;
                    var bufListField = AccessTools.Field(buffer.GetType(), "buffer");
                    var bufList = bufListField?.GetValue(buffer) as IList;
                    if (bufList != null)
                    {
                        foreach (object? bcItem in bufList)
                        {
                            if (bcItem == null) continue;
                            var scoreField = AccessTools.Field(bcItem.GetType(), "score");
                            if (scoreField == null) continue;
                            float s = (float)(scoreField.GetValue(bcItem) ?? 0f);
                            scoreField.SetValue(bcItem, s * viewMult);
                        }
                        Plugin.Logger.LogInfo(
                            $"[ContentEvaluatorPatch] Progressive Views: applied {viewMult:F1}× " +
                            $"score multiplier (level {viewLevel}) to {bufList.Count} buffer entries.");
                    }
                }

                // ── Filming checks — master client only ───────────────────────────────
                // AP checks and notifications are authoritative on the host; non-master
                // clients rely on RPCA_BroadcastAPCheckNotification for their popups.
                if (PhotonNetwork.IsMasterClient)
                    FireFilmingChecks(buffer);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ContentEvaluatorPatch] Exception in filming postfix: {ex}");
            }
        }

        // ------------------------------------------------------------------
        // Per-dive entity tracking — tier advancement guard
        // ------------------------------------------------------------------

        /// <summary>
        /// Entities (base AP location names) that have already been counted as
        /// filmed during the current dive.  Cleared at the start of each new
        /// dive by <see cref="ResetDailyFilmingState"/> so the same monster or
        /// artifact can only advance the tier counter once per day.
        /// </summary>
        private static readonly HashSet<string> _monstersFilmedThisDay =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Clears the per-dive entity set.
        /// Called from <c>SurfaceApplyRoomPropsPatch.RPCM_StartGamePostfix</c>
        /// (LateJoinSyncPatch.cs) at the start of every dive so the same
        /// monster/artifact can only count once toward tier advancement per day.
        /// </summary>
        public static void ResetDailyFilmingState()
        {
            int count = _monstersFilmedThisDay.Count;
            _monstersFilmedThisDay.Clear();
            Plugin.Logger.LogInfo(
                $"[ContentEvaluatorPatch] Daily filming state reset " +
                $"({count} entr{(count == 1 ? "y" : "ies")} cleared).");
        }

        // ------------------------------------------------------------------
        // Filming detection — walks ContentBuffer.buffer
        // ------------------------------------------------------------------

        /// <summary>
        /// Iterates the ContentBuffer's <c>buffer</c> list (List&lt;BufferedContent&gt;),
        /// navigates the field chain:
        ///   BufferedContent.frame → ContentEventFrame.contentEvent → ContentEvent subclass
        ///
        /// For each event it:
        ///   1. Calls GetName() and GetID() and logs both for diagnostic confirmation.
        ///   2. Reads contentEvent.GetID() as a ushort for direct monster ID matching.
        ///   3. Detects ArtifactContentEvent by class name; reads the nested PropContent
        ///      via (contentEvent as ArtifactContentEvent).content.name (UnityEngine.Object.name)
        ///      to identify the specific artifact filmed.
        ///   4. Calls TryFireEntityCheck with all gathered context to resolve and
        ///      send the AP location check with 3-level priority fallback.
        ///
        /// A per-call <see cref="HashSet{T}"/> deduplicates so each AP location is
        /// sent at most once per EvaluateRecording invocation.
        /// </summary>
        private static void FireFilmingChecks(object contentBuffer)
        {
            var fired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Type bufferType = contentBuffer.GetType();

            // ContentBuffer.buffer → List<BufferedContent>
            var bufferListField = AccessTools.Field(bufferType, "buffer");
            if (bufferListField == null)
            {
                Plugin.Logger.LogWarning(
                    "[ContentEvaluatorPatch] Could not find 'buffer' field on ContentBuffer. " +
                    "Filming checks skipped.");
                return;
            }

            var bufferList = bufferListField.GetValue(contentBuffer) as IEnumerable;
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

                Type eventType = contentEvent.GetType();

                // --- Resolve ushort event ID via contentEvent.GetID() ---
                ushort eventId = 0;
                var getIdMethod = eventType.GetMethod(
                    "GetID",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getIdMethod != null)
                {
                    var rawId = getIdMethod.Invoke(contentEvent, null);
                    if (rawId != null)
                    {
                        try { eventId = Convert.ToUInt16(rawId); }
                        catch { /* ignore overflow; eventId stays 0 */ }
                    }
                }

                // --- Resolve event name via contentEvent.GetName() ---
                string eventName = string.Empty;
                var getNameMethod = eventType.GetMethod(
                    "GetName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (getNameMethod != null)
                {
                    eventName = getNameMethod.Invoke(contentEvent, null)?.ToString() ?? string.Empty;
                }

                // Buffer scan — log GetType().Name for every event so we can see exactly
                // which event types are present in the recording.
                string rawName = eventType.Name;
                Plugin.Logger.LogInfo(
                    $"[ContentEvaluatorPatch] Buffer scan — type='{rawName}', " +
                    $"GetName()='{eventName}', GetID()={eventId}");

                // For artifact- or prop-related events, dump every field at every
                // inheritance level so we can pinpoint where 'Skull' / 'Ribcage' etc.
                // is actually stored at runtime.
                bool isArtifactOrProp =
                    rawName.IndexOf("artifact", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawName.IndexOf("prop",     StringComparison.OrdinalIgnoreCase) >= 0;

                if (isArtifactOrProp)
                {
                    Plugin.Logger.LogInfo(
                        "[ContentEvaluatorPatch] >> Artifact/prop event — dumping all fields:");
                    for (Type t = eventType; t != null && t != typeof(object); t = t.BaseType)
                    {
                        foreach (var fi in t.GetFields(
                            BindingFlags.Instance  |
                            BindingFlags.Public    |
                            BindingFlags.NonPublic |
                            BindingFlags.DeclaredOnly))
                        {
                            try
                            {
                                object? fieldVal = fi.GetValue(contentEvent);
                                string valStr = fieldVal is UnityEngine.Object uo2
                                    ? $"UnityObject(name='{uo2.name}', type='{uo2.GetType().Name}')"
                                    : (fieldVal?.ToString() ?? "null");
                                Plugin.Logger.LogInfo(
                                    $"[ContentEvaluatorPatch]   [{t.Name}].{fi.Name} = {valStr}");
                            }
                            catch (Exception fex)
                            {
                                Plugin.Logger.LogInfo(
                                    $"[ContentEvaluatorPatch]   [{t.Name}].{fi.Name} = <error: {fex.Message}>");
                            }
                        }
                    }
                }

                // --- Detect ArtifactContentEvent: extract PropContent asset name ---
                // Logs confirm ArtifactContentEvent has TWO relevant fields:
                //   • artifact — specific asset name (e.g. 'Ribcage')  ← preferred
                //   • content  — generic category name (e.g. 'Bones')  ← fallback
                // Search order: artifact → content → item
                string? artifactDisplayName = null;

                if (rawName.Equals("ArtifactContentEvent", StringComparison.Ordinal))
                {
                    FieldInfo? contentField = null;
                    string? resolvedFieldName = null;
                    foreach (string candidateName in new[] { "artifact", "content", "item" })
                    {
                        contentField = AccessTools.Field(eventType, candidateName);
                        if (contentField != null)
                        {
                            resolvedFieldName = candidateName;
                            break;
                        }
                    }

                    if (contentField != null)
                    {
                        Plugin.Logger.LogInfo(
                            $"[ContentEvaluatorPatch] ArtifactContentEvent: resolved field name = '{resolvedFieldName}'");
                        var propContent = contentField.GetValue(contentEvent);
                        if (propContent is UnityEngine.Object uo)
                        {
                            artifactDisplayName = uo.name;
                            Plugin.Logger.LogInfo(
                                $"[ContentEvaluatorPatch] ArtifactContentEvent: " +
                                $"assetName='{artifactDisplayName}', ID={eventId}");
                        }
                        else
                        {
                            Plugin.Logger.LogInfo(
                                $"[ContentEvaluatorPatch] ArtifactContentEvent: " +
                                $"field '{resolvedFieldName}' value is not UnityEngine.Object " +
                                $"(actual type: '{propContent?.GetType().Name ?? "null"}')");
                        }
                    }
                    else
                    {
                        // None of the candidate names matched — log available fields for diagnosis.
                        var allFields = eventType.GetFields(
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        string fieldList = string.Join(", ", Array.ConvertAll(allFields, f => f.Name));
                        Plugin.Logger.LogWarning(
                            $"[ContentEvaluatorPatch] ArtifactContentEvent: could not find " +
                            $"'content'/'item'/'artifact' field. Available fields: [{fieldList}]");
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
        ///      asset name in <see cref="FilmingLocationData.EntityTypeToLocation"/>.
        ///   3. <b>Class name match</b>   — looks up the raw runtime class name, then
        ///      retries after stripping a trailing "ContentEvent" suffix, to handle both
        ///      "Slurper" and "SlurperContentEvent" style names for any event not
        ///      already resolved above.
        ///
        /// Fires at most once per distinct location per <paramref name="fired"/> set.
        /// After sending, broadcasts the check notification to all clients via
        /// <see cref="BroadcastAPCheckNotification"/>.
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

            // ── Priority 2: Artifact asset-name match ────────────────────────────────
            // Only populated when FireFilmingChecks detected an ArtifactContentEvent.
            // Uses UnityEngine.Object.name (asset name) as the lookup key.
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

            // ── Per-recording dedup ─────────────────────────────────────────────────
            // Prevents firing multiple times for the same entity appearing in one
            // ContentBuffer (e.g. a monster in several frames of the same recording).
            if (fired.Contains(locationName)) return;
            fired.Add(locationName);

            string baseLocationName = locationName;

            // ── Per-dive dedup ──────────────────────────────────────────────────────
            // Filming the same monster/artifact more than once during a single dive
            // only counts as ONE new encounter for tier-progression purposes.
            // The set is reset at the start of each dive (ResetDailyFilmingState).
            if (_monstersFilmedThisDay.Contains(baseLocationName))
            {
                Plugin.Logger.LogDebug(
                    $"[ContentEvaluatorPatch] '{baseLocationName}' already filmed this dive — " +
                    "skipping tier advancement.");
                return;
            }
            _monstersFilmedThisDay.Add(baseLocationName);

            // ── Tier selection ──────────────────────────────────────────────────────
            // Send the first unchecked tier in order: base → tier 2 → tier 3.
            // Tier 2/3 are only attempted when the AP world was generated with
            // monster_tiers enabled (APSave.saveData.monsterTiersEnabled).
            // This avoids sending bogus checks for non-existent locations when
            // the option is off.
            bool tiersOn = APSave.saveData.monsterTiersEnabled;

            long locIdBase = LocationData.GetId(baseLocationName);
            long locId2    = tiersOn ? LocationData.GetId(baseLocationName + " 2") : -1L;
            long locId3    = tiersOn ? LocationData.GetId(baseLocationName + " 3") : -1L;

            string? locationToSend = null;

            if (locIdBase >= 0 && !APSave.IsLocationChecked(locIdBase))
                locationToSend = baseLocationName;
            else if (locId2 >= 0 && !APSave.IsLocationChecked(locId2))
                locationToSend = baseLocationName + " 2";
            else if (locId3 >= 0 && !APSave.IsLocationChecked(locId3))
                locationToSend = baseLocationName + " 3";

            if (locationToSend == null)
            {
                Plugin.Logger.LogDebug(
                    $"[ContentEvaluatorPatch] All tiers for '{baseLocationName}' already checked.");
                return;
            }

            long locIdToSend = LocationData.GetId(locationToSend);
            if (locIdToSend < 0)
            {
                Plugin.Logger.LogDebug(
                    $"[ContentEvaluatorPatch] '{locationToSend}' not in AP location table.");
                return;
            }

            Plugin.Logger.LogInfo($"[ContentEvaluatorPatch] Filming check → {locationToSend}");
            Plugin.SendCheck(locIdToSend);

            // Broadcast popup to all clients (non-master clients show it here;
            // master already saw it from ActivateCheck's local ShowLocationFound).
            BroadcastAPCheckNotification(locationToSend);
        }

        // ------------------------------------------------------------------
        // Network notification helper
        // ------------------------------------------------------------------

        /// <summary>
        /// Broadcasts an AP check notification to ALL connected clients via a
        /// Photon RPC on <c>SurfaceNetworkHandler</c>'s PhotonView.
        ///
        /// <para>
        /// Called on the <b>master client only</b> (guarded by the
        /// <c>IsMasterClient</c> checks in <see cref="Postfix"/> and
        /// <see cref="FilmingPostfix"/>).  The master already sees the notification
        /// locally from <c>ActivateCheck → APNotificationUI.ShowLocationFound</c>;
        /// the RPC receiver (<see cref="SurfaceAPNotificationBroadcaster"/>) skips
        /// display on master so only non-master clients (e.g. 'Reggi') see the popup.
        /// </para>
        ///
        /// <para>
        /// A <see cref="SurfaceAPNotificationBroadcaster"/> component is lazily
        /// added to <c>SurfaceNetworkHandler.Instance.gameObject</c> so that
        /// Photon can dispatch the named RPC to a MonoBehaviour on the same
        /// GameObject as the PhotonView.
        /// </para>
        /// </summary>
        private static void BroadcastAPCheckNotification(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return;

            try
            {
                var surface = SurfaceNetworkHandler.Instance;
                if (surface == null) return;

                // Lazily attach the broadcaster so the RPC target MonoBehaviour exists.
                if (surface.GetComponent<SurfaceAPNotificationBroadcaster>() == null)
                    surface.gameObject.AddComponent<SurfaceAPNotificationBroadcaster>();

                surface.photonView.RPC(
                    "RPCA_BroadcastAPCheckNotification",
                    RpcTarget.All,
                    locationName);

                Plugin.Logger.LogDebug(
                    $"[ContentEvaluatorPatch] Broadcast AP notification RPC: '{locationName}'");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[ContentEvaluatorPatch] BroadcastAPCheckNotification failed: {ex.Message}");
            }
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

    // =========================================================================
    // SurfaceAPNotificationBroadcaster
    //
    // A lightweight MonoBehaviour attached at runtime to SurfaceNetworkHandler's
    // GameObject (which already owns the game's PhotonView for the surface scene).
    // Photon dispatches a PhotonView RPC to every MonoBehaviour on the same
    // GameObject, so attaching this component is sufficient for the master client
    // to call:
    //
    //   SurfaceNetworkHandler.Instance.photonView.RPC(
    //       "RPCA_BroadcastAPCheckNotification", RpcTarget.All, locName)
    //
    // and have it execute here on every connected client — including late joiners
    // like 'Reggi' who would otherwise miss the notification because
    // ContentEvaluator.EvaluateRecording only ran on the master's machine.
    //
    // The master client skips the popup in the RPC handler because ActivateCheck
    // already displayed it locally; only non-master clients call ShowLocationFound.
    // =========================================================================

    /// <summary>
    /// Receives the master client's AP check notification and displays the
    /// "Location Found!" popup on every non-master Photon client.
    /// </summary>
    internal class SurfaceAPNotificationBroadcaster : MonoBehaviour
    {
        /// <summary>
        /// Called on <b>all</b> clients (including the master) by the master
        /// client after it has fired an AP location check during filming or
        /// extraction evaluation.
        ///
        /// <para>
        /// The master client already sees the notification from
        /// <c>ActivateCheck → APNotificationUI.ShowLocationFound</c>, so this
        /// method skips display if the receiver is the master.  Non-master
        /// clients (e.g. 'Reggi') display the popup here.
        /// </para>
        /// </summary>
        [PunRPC]
        public void RPCA_BroadcastAPCheckNotification(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return;

            // Master already displayed the notification locally via ActivateCheck.
            if (PhotonNetwork.IsMasterClient) return;

            Plugin.Logger.LogInfo(
                $"[SurfaceAPNotificationBroadcaster] Showing AP check notification: '{locationName}'");

            APNotificationUI.ShowLocationFound(locationName);
        }
    }
}
