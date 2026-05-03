// Patches/ProgressionStatsPatch.cs
// Applies Progressive Stamina, Progressive Stamina Regen, and Progressive
// Camera upgrades via Harmony Postfixes.
//
// ─────────────────────────────────────────────────────────────────────────────
// STAMINA — StaminaUpgradePatch
//   Target : Player.Awake (private Unity lifecycle method)
//
//   WHY Player.Awake:
//   PlayerController.Start() sets player.data.currentStamina = maxStamina when
//   the player spawns.  By patching Player.Awake (which fires before all Start()
//   calls), we write the upgraded value into PlayerController.maxStamina before
//   PlayerController.Start() reads it.  The correct stamina cap propagates to
//   PlayerData automatically — no additional CurrentStamina assignment needed
//   at spawn time.
//
//   Formula : maxStamina = baseMaxStamina × (1 + 0.25 × staminaUpgradeLevel)
//             — matches issue #13 (level 4 = 200 % of vanilla cap).  We capture
//             baseMaxStamina from the prefab on the very first Player.Awake
//             (before our patch mutates it).  Hardcoding 100 was wrong: the
//             vanilla PlayerController prefab ships with maxStamina ≈ 10, so
//             100 + level × 25 produced an effectively infinite sprint bar.
//   Sync    : ProgressionStatsPatch.ApplyStaminaUpgrade(level) re-applies to the
//             live local player when an item arrives mid-game.
//
// ─────────────────────────────────────────────────────────────────────────────
// STAMINA REGEN — StaminaRegenUpgradePatch
//   Target : PlayerController.Update (Postfix)
//
//   WHY PlayerController.Update (and not a field write):
//   PlayerController declares `public float staminaRegRate = 2f;` but it is
//   never read — the actual regen formula is hardcoded inside Update as
//   `currentStamina = Mathf.MoveTowards(currentStamina, maxStamina, Time.deltaTime)`,
//   gated on `data.sinceSprint > 1f`.  Writing to `staminaRegRate` is a no-op,
//   so we instead Postfix Update with the same gating and add an extra
//   `0.5 × level × Time.deltaTime` of stamina per frame.  Combined with the
//   vanilla addition, total regen per second = base × (1 + 0.5 × level), which
//   matches the issue's `BaseRegenRate × (1 + 0.5 × StaminaRegenLevel)` formula
//   exactly (level 1 = 150 %, level 2 = 200 %).
//
//   Local player only: remote players' stamina is server-authoritative; their
//   regen is owned by their own client.
//
//   No sync helper: regen is continuous, so the new level takes effect on the
//   next frame after staminaRegenUpgradeLevel is incremented — no day restart
//   or mid-game reapplication step is needed.
//
// ─────────────────────────────────────────────────────────────────────────────
// CAMERA — CameraUpgradePatch
//   Target : VideoCamera.ConfigItem(ItemInstanceData, PhotonView)
//
//   WHY ConfigItem:
//   ConfigItem is the per-item initialisation hook called when a VideoCamera
//   is configured for a player.  It either loads an existing VideoInfoEntry
//   (battery state) or creates a new one with maxTime = timeLeft = 90 s.
//   Patching as Postfix guarantees m_recorderInfoEntry is always non-null by
//   the time our code runs.
//
//   Formula : maxTime = 90 + cameraUpgradeLevel × 30  (timeLeft reset to maxTime)
//   Sync    : ProgressionStatsPatch.ApplyCameraUpgrade(level) iterates all live
//             VideoCamera instances when an item arrives mid-day.
//
// ─────────────────────────────────────────────────────────────────────────────
// Both sync helpers are public static methods on ProgressionStatsPatch and are
// called from ItemData.HandleReceivedItem so the buff takes effect immediately
// without requiring a day restart.

using System.Reflection;
using HarmonyLib;
using ContentWarningArchipelago.Core;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // STAMINA UPGRADE PATCH
    // =========================================================================

    /// <summary>
    /// Postfix on <c>Player.Awake</c>.
    /// Writes the upgraded <c>maxStamina</c> value into <c>PlayerController</c>
    /// before <c>PlayerController.Start()</c> copies it into
    /// <c>player.data.currentStamina</c>.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Awake")]
    internal static class StaminaUpgradePatch
    {
        // Captured from the prefab on the very first Player.Awake — before our
        // own Postfix mutates controller.maxStamina.  Unity instantiates the
        // PlayerController fresh from the prefab on every spawn, so this field
        // always sees the unmodified vanilla value.  -1 means "not yet seen".
        internal static float baseMaxStamina = -1f;

        [HarmonyPostfix]
        private static void Postfix(Player __instance)
        {
            // refs.controller is not set until Player.Start(); use GetComponent.
            var controller = __instance.GetComponent<PlayerController>();
            if (controller == null) return;

            // Capture the vanilla cap once.  Done outside the connected/level
            // guards so we still record it even if the player spawns before
            // connecting to AP, or before any Progressive Stamina arrives.
            if (baseMaxStamina < 0f) baseMaxStamina = controller.maxStamina;

            if (!Plugin.connection.connected) return;

            int level = APSave.saveData.staminaUpgradeLevel;
            if (level <= 0) return;

            float newMax = baseMaxStamina * (1f + 0.25f * level);
            controller.maxStamina = newMax;

            Plugin.Logger.LogInfo(
                $"[StaminaUpgradePatch] Player.Awake — maxStamina set to {newMax} " +
                $"(base {baseMaxStamina}, level {level}). PlayerController.Start " +
                $"will initialise currentStamina.");
        }
    }

    // =========================================================================
    // STAMINA REGEN UPGRADE PATCH
    // =========================================================================

    /// <summary>
    /// Postfix on <c>PlayerController.Update</c>.  Adds extra stamina regen each
    /// frame so the per-second rate becomes <c>base × (1 + 0.5 × level)</c>.
    /// Mirrors the vanilla regen gating (only after <c>sinceSprint &gt; 1f</c>,
    /// only while below max) and runs only for the local player.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "Update")]
    internal static class StaminaRegenUpgradePatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerController __instance)
        {
            if (!Plugin.connection.connected) return;

            int level = APSave.saveData.staminaRegenUpgradeLevel;
            if (level <= 0) return;

            // Only apply to the local player — remote players' stamina is
            // owned by their own client.
            if (Player.localPlayer == null) return;
            if (Player.localPlayer.GetComponent<PlayerController>() != __instance) return;

            var data = Player.localPlayer.data;
            if (data == null) return;

            // Match vanilla gating (PlayerController.Update body).
            if (data.sinceSprint <= 1f) return;
            if (data.currentStamina >= __instance.maxStamina) return;

            float extra = 0.5f * level * Time.deltaTime;
            data.currentStamina = Mathf.Min(__instance.maxStamina, data.currentStamina + extra);
        }
    }

    // =========================================================================
    // CAMERA UPGRADE PATCH
    // =========================================================================

    /// <summary>
    /// Postfix on <c>VideoCamera.ConfigItem</c>.
    /// After ConfigItem creates or loads the <c>VideoInfoEntry</c>, overrides
    /// <c>maxTime</c> and <c>timeLeft</c> to <c>90 + cameraUpgradeLevel × 30</c>
    /// so every camera starts with the fully upgraded, fully charged battery.
    /// </summary>
    [HarmonyPatch]
    internal static class CameraUpgradePatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("VideoCamera");
            if (type == null)
            {
                Plugin.Logger.LogWarning(
                    "[CameraUpgradePatch] Could not find type 'VideoCamera'. Patch skipped.");
                return null;
            }

            var method = AccessTools.Method(type, "ConfigItem");
            if (method != null)
            {
                Plugin.Logger.LogInfo(
                    $"[CameraUpgradePatch] Patching {type.Name}.{method.Name}");
                return method;
            }

            Plugin.Logger.LogWarning(
                "[CameraUpgradePatch] Could not find 'ConfigItem' on VideoCamera. Patch skipped.");
            return null;
        }

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Plugin.connection.connected) return;

            int level = APSave.saveData.cameraUpgradeLevel;
            if (level <= 0) return;

            ApplyToInstance(__instance, level);
        }

        // ------------------------------------------------------------------
        // Internal helper — shared by Postfix and mid-game sync.
        // ------------------------------------------------------------------

        /// <summary>
        /// Applies the battery upgrade to a single <c>VideoCamera</c> instance
        /// by writing to its private <c>m_recorderInfoEntry</c> (a
        /// <c>VideoInfoEntry</c> with public fields <c>maxTime</c> and
        /// <c>timeLeft</c>) via <c>AccessTools.Field</c>.
        /// </summary>
        internal static void ApplyToInstance(object cameraInstance, int level)
        {
            // Reach into VideoCamera.m_recorderInfoEntry (private field).
            var entryField = AccessTools.Field(cameraInstance.GetType(), "m_recorderInfoEntry");
            if (entryField == null)
            {
                Plugin.Logger.LogWarning(
                    "[CameraUpgradePatch] Could not find 'm_recorderInfoEntry' on VideoCamera.");
                return;
            }

            var entry = entryField.GetValue(cameraInstance);
            if (entry == null)
            {
                Plugin.Logger.LogDebug(
                    "[CameraUpgradePatch] m_recorderInfoEntry is null — camera not fully initialised.");
                return;
            }

            // VideoInfoEntry.maxTime and .timeLeft are public float fields
            // (confirmed from VideoCamera.cs: timeLeft = 90f, maxTime = 90f).
            var entryType   = entry.GetType();
            var maxTimeField  = AccessTools.Field(entryType, "maxTime");
            var timeLeftField = AccessTools.Field(entryType, "timeLeft");

            if (maxTimeField == null || timeLeftField == null)
            {
                Plugin.Logger.LogWarning(
                    "[CameraUpgradePatch] Could not find 'maxTime'/'timeLeft' on VideoInfoEntry.");
                return;
            }

            float newMax         = 90f + level * 30f;
            float currentMax     = (float)maxTimeField.GetValue(entry);
            float currentTimeLeft = (float)timeLeftField.GetValue(entry);

            // Only update when maxTime has not yet been set to the upgraded value.
            // ConfigItem fires every time the camera is equipped, so we must not
            // unconditionally reset timeLeft or the camera refills film on every equip.
            if (Mathf.Approximately(currentMax, newMax))
            {
                Plugin.Logger.LogDebug(
                    $"[CameraUpgradePatch] Battery already at maxTime={newMax} s — skipping.");
                return;
            }

            // maxTime needs to change — apply the upgrade.
            maxTimeField.SetValue(entry, newMax);

            // Only reset timeLeft to the new maximum when the camera is "fresh":
            //   • timeLeft is still at the vanilla default (90 s) — never been used, OR
            //   • maxTime was still at the vanilla default (90 s) — freshly spawned camera
            //     that has never had an upgrade applied to it before.
            // For cameras already in use (timeLeft < 90 s) we preserve the remaining film
            // so that a re-equip or a new upgrade level does not silently refill the battery.
            const float vanillaDefault = 90f;
            bool isFresh = Mathf.Approximately(currentTimeLeft, vanillaDefault)
                           || Mathf.Approximately(currentMax, vanillaDefault);
            if (isFresh)
                timeLeftField.SetValue(entry, newMax);

            // Mark dirty so Photon syncs the updated values to all clients.
            AccessTools.Method(entryType, "SetDirty")?.Invoke(entry, null);

            float loggedTimeLeft = isFresh ? newMax : currentTimeLeft;
            Plugin.Logger.LogInfo(
                $"[CameraUpgradePatch] Battery → maxTime={newMax} s, " +
                $"timeLeft={loggedTimeLeft} s (level {level}).");
        }
    }

    // =========================================================================
    // PUBLIC SYNC HELPERS
    // Called from ItemData.HandleReceivedItem when a progressive item arrives
    // while the player is already in-game, so the buff applies immediately
    // without requiring a day restart.
    // =========================================================================

    /// <summary>
    /// Static helpers that re-apply progressive stat upgrades to live game
    /// objects mid-game.  Called by <c>ItemData.HandleReceivedItem</c>.
    /// </summary>
    public static class ProgressionStatsPatch
    {
        /// <summary>
        /// Re-applies the Stamina upgrade to the currently alive local player.
        /// Sets both <c>PlayerController.maxStamina</c> and
        /// <c>Player.localPlayer.data.currentStamina</c> to the new maximum so
        /// the player immediately feels the extra sprint capacity.
        /// </summary>
        /// <param name="level">Current <c>staminaUpgradeLevel</c> (after increment).</param>
        public static void ApplyStaminaUpgrade(int level)
        {
            if (Player.localPlayer == null)
            {
                Plugin.Logger.LogDebug(
                    "[ProgressionStatsPatch] ApplyStaminaUpgrade: localPlayer is null — " +
                    "buff will apply on next Player.Awake.");
                return;
            }

            var controller = Player.localPlayer.GetComponent<PlayerController>();
            if (controller == null) return;

            // Mid-game items always arrive after the local player has spawned,
            // so StaminaUpgradePatch.Postfix has already captured baseMaxStamina.
            // Fallback to the vanilla C# default of 10 if (somehow) it has not.
            float baseMax = StaminaUpgradePatch.baseMaxStamina > 0f
                ? StaminaUpgradePatch.baseMaxStamina
                : 10f;
            float newMax = baseMax * (1f + 0.25f * level);
            controller.maxStamina = newMax;

            // Also update currentStamina so the bar refills to the new cap immediately.
            Player.localPlayer.data.currentStamina = newMax;

            Plugin.Logger.LogInfo(
                $"[ProgressionStatsPatch] Stamina sync — base={baseMax}, " +
                $"maxStamina={newMax}, currentStamina={newMax} (level {level}).");
        }

        /// <summary>
        /// Re-applies the Camera battery upgrade to every <c>VideoCamera</c>
        /// currently active in the scene.  Updates <c>maxTime</c> and resets
        /// <c>timeLeft</c> to the new maximum so cameras in the world are
        /// immediately charged to the upgraded capacity.
        /// </summary>
        /// <param name="level">Current <c>cameraUpgradeLevel</c> (after increment).</param>
        public static void ApplyCameraUpgrade(int level)
        {
            var cameras = Object.FindObjectsOfType<VideoCamera>();
            if (cameras == null || cameras.Length == 0)
            {
                Plugin.Logger.LogDebug(
                    "[ProgressionStatsPatch] ApplyCameraUpgrade: no VideoCamera instances " +
                    "in scene — upgrade will apply on next ConfigItem call.");
                return;
            }

            foreach (var cam in cameras)
            {
                CameraUpgradePatch.ApplyToInstance(cam, level);
            }

            Plugin.Logger.LogInfo(
                $"[ProgressionStatsPatch] Camera sync — updated {cameras.Length} camera(s) " +
                $"to {90 + level * 30} s battery (level {level}).");
        }
    }
}
