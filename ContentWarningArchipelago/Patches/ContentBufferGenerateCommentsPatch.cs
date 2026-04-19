// Patches/ContentBufferGenerateCommentsPatch.cs
//
// Patches ContentBuffer.GenerateComments() to fire Archipelago filming checks
// for every entity (monster or artifact) the game officially "saw" and generated
// audience comments for.
//
// WHY THIS METHOD:
//   ContentBuffer.GenerateComments() iterates buffer (List<BufferedContent>) and
//   for each entry calls contentEvent.GenerateComment().  A non-null Comment is
//   returned only when the game considers the entity valid/captured.  The method
//   also sets comment.EventID = contentEvent.GetID(), giving us the ushort type ID
//   on each entry.  This is the most reliable hook for "the game saw this entity"
//   because it runs after all scoring and deduplication is complete.
//
// REPLACES the filming-check logic that was in ContentEvaluatorPatch, which was
// failing to resolve artifact entity names (ArtifactContentEvent uses the Unity
// asset Object.name, not a 'displayName' field that does not exist on PropContent).
//
// CONTENT EVENT DISPATCH LOGIC:
//   • IDs 1000–1045 → specific MonsterContentEvent subclasses
//     Looked up directly in FilmingLocationData.EntityIdToLocation.
//   • Other IDs → ArtifactContentEvent or PropContentEvent at runtime.
//     ArtifactContentEvent detected by class name; artifact identity read from
//     the nested PropContent via UnityEngine.Object.name (the asset name).
//   • Fallback → raw class name / stripped class name match in EntityTypeToLocation.
//
// Field chain confirmed from ContentBuffer.cs game reference:
//   ContentBuffer.buffer           — List<BufferedContent>  (public field)
//   BufferedContent.frame          — ContentEventFrame      (public field)
//   ContentEventFrame.contentEvent — ContentEvent subclass  (public field)
//   ArtifactContentEvent.content   — PropContent            (public field)
//   PropContent is a ScriptableObject; use Object.name for the asset name.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ContentWarningArchipelago.Data;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // Patch — ContentBuffer.GenerateComments()
    //
    // Signature (ContentBuffer.cs):
    //   public List<Comment> GenerateComments()
    //
    // Called after footage is evaluated so the game can build the comment list
    // shown on the extraction screen.  Every entity in buffer that produces a
    // non-null Comment was officially captured; we fire an AP filming check here.
    // =========================================================================
    [HarmonyPatch]
    public static class ContentBufferGenerateCommentsPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("ContentBuffer");
            if (type == null)
            {
                Plugin.Logger.LogWarning(
                    "[GenerateCommentsPatch] Could not find type 'ContentBuffer'. " +
                    "Filming checks will not fire.");
                return null;
            }

            var method = AccessTools.Method(type, "GenerateComments");
            if (method != null)
            {
                Plugin.Logger.LogInfo($"[GenerateCommentsPatch] Patching {type.Name}.{method.Name}");
                return method;
            }

            Plugin.Logger.LogWarning(
                "[GenerateCommentsPatch] Could not find 'GenerateComments' on ContentBuffer. " +
                "Filming checks will not fire. Verify the method name in Assembly-CSharp.");
            return null;
        }

        /// <summary>
        /// Postfix fires after ContentBuffer.GenerateComments() returns.
        /// <paramref name="__instance"/> is the ContentBuffer whose buffer list
        /// was iterated by the game.  We walk the same buffer to detect and fire
        /// AP filming checks for every entity the game officially captured.
        /// </summary>
        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (!Plugin.connection.connected) return;

            try
            {
                FireFilmingChecks(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[GenerateCommentsPatch] Exception in postfix: {ex}");
            }
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
        ///   1. Reads contentEvent.GetID() as a ushort for direct monster ID matching.
        ///   2. Detects ArtifactContentEvent by class name; uses reflection to access
        ///      the nested PropContent object and reads its UnityEngine.Object.name
        ///      (the asset name) to identify the specific artifact filmed.
        ///   3. Calls TryFireEntityCheck with all gathered context to resolve and
        ///      send the AP location check with 3-level priority fallback.
        ///
        /// A per-call <see cref="HashSet{T}"/> deduplicates so each AP location is
        /// sent at most once per GenerateComments invocation.
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
                    "[GenerateCommentsPatch] Could not find 'buffer' field on ContentBuffer. " +
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

                // --- Resolve ushort event ID via contentEvent.GetID() ---
                ushort eventId = 0;
                var getIdMethod = contentEvent.GetType().GetMethod(
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

                // --- Detect ArtifactContentEvent: extract PropContent asset name ---
                // ArtifactContentEvent is generated by ContentEventIDMapper when the ID
                // resolves to a PropContent with isArtifact == true.  The class name is
                // always "ArtifactContentEvent" regardless of which artifact it is, so
                // we must read the nested 'content' field to find the artifact identity.
                // PropContent is a ScriptableObject and has no 'displayName' field;
                // the correct identifier is UnityEngine.Object.name (the asset name).
                string? artifactDisplayName = null;
                string rawName = contentEvent.GetType().Name;

                if (rawName.Equals("ArtifactContentEvent", StringComparison.Ordinal))
                {
                    var contentField = AccessTools.Field(contentEvent.GetType(), "content");
                    if (contentField != null)
                    {
                        var propContent = contentField.GetValue(contentEvent);
                        if (propContent is UnityEngine.Object uo)
                        {
                            artifactDisplayName = uo.name;
                            Plugin.Logger.LogDebug(
                                $"[GenerateCommentsPatch] ArtifactContentEvent detected: " +
                                $"assetName='{artifactDisplayName}', ID={eventId}");
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
        ///      asset name in <see cref="FilmingLocationData.EntityTypeToLocation"/>.
        ///   3. <b>Class name match</b>   — looks up the raw runtime class name, then
        ///      retries after stripping a trailing "ContentEvent" suffix, to handle both
        ///      "Slurper" and "SlurperContentEvent" style names for any event not
        ///      already resolved above.
        ///
        /// Fires at most once per distinct location per <paramref name="fired"/> set.
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
                    $"[GenerateCommentsPatch] No AP location for: class='{rawName}', ID={id}, " +
                    $"artifactName='{artifactDisplayName ?? "n/a"}'. " +
                    $"Add to FilmingLocationData if this is a new monster/artifact.");
                return;
            }

            // Fire once per distinct location per GenerateComments call.
            if (fired.Contains(locationName)) return;
            fired.Add(locationName);

            long locId = LocationData.GetId(locationName);
            if (locId < 0)
            {
                Plugin.Logger.LogDebug($"[GenerateCommentsPatch] '{locationName}' not in AP location table.");
                return;
            }

            Plugin.Logger.LogInfo($"[GenerateCommentsPatch] Filming check → {locationName}");
            Plugin.SendCheck(locId);
        }
    }
}
