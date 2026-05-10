// Patches/ViewsTrackerPatch.cs
//
// Tracks two view counters that drive AP location checks:
//   • lifetimeViews    — cumulative across the whole AP slot.  Crossing each
//                        ViewMilestones table entry fires the matching
//                        "Reached N Total Views" check.
//   • currentQuotaViews — views earned within the current 3-day quota cycle.
//                         Crossing 1,000,000 fires "Viral Sensation Achieved".
//                         Reset on quota pass / fail.
//
// Hooks (split into separate classes so PatchAll discovers each):
//   • RoomStatsHolder.AddQuota(int)   — score → views, advance counters, fire.
//   • SurfaceNetworkHandler.NewWeek   — fire "Met Quota N", reset per-quota.
//   • SurfaceNetworkHandler.RPC_QuotaFailed — reset per-quota on failure.
//
// AddQuota only fires on the master client (UploadCompleteState.PlayVideo),
// but RoomStatsHolder.AddQuota itself is reachable from the console command
// in non-master debugging builds, so we belt-and-braces guard with
// PhotonNetwork.IsMasterClient too.

using System;
using System.Reflection;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using HarmonyLib;
using Photon.Pun;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // RoomStatsHolder.AddQuota — score → views, fire milestones / viral sensation
    // =========================================================================
    [HarmonyPatch]
    internal static class AddQuotaPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("RoomStatsHolder");
            if (type == null)
            {
                Plugin.Logger.LogWarning(
                    "[ViewsTracker] RoomStatsHolder type not found — view-milestone checks disabled.");
                return null;
            }
            var method = AccessTools.Method(type, "AddQuota", new[] { typeof(int) });
            if (method == null)
            {
                Plugin.Logger.LogWarning(
                    "[ViewsTracker] RoomStatsHolder.AddQuota(int) not found — view-milestone checks disabled.");
            }
            return method;
        }

        [HarmonyPostfix]
        static void Postfix(int quotaToAdd)
        {
            if (!Plugin.connection.connected) return;
            if (!PhotonNetwork.IsMasterClient) return;
            if (quotaToAdd <= 0) return;

            try
            {
                int day = ViewsTrackerHelpers.TryGetCurrentDay();
                if (day <= 0) return;

                int viewsAdded = ViewsTrackerHelpers.SafeGetScoreToViews(quotaToAdd, day);
                if (viewsAdded <= 0) return;

                var s = APSave.saveData;
                long prevLifetime  = s.lifetimeViews;
                long prevQuota     = s.currentQuotaViews;
                s.lifetimeViews    += viewsAdded;
                s.currentQuotaViews += viewsAdded;

                Plugin.Logger.LogInfo(
                    $"[ViewsTracker] +{viewsAdded} views (day {day}) — " +
                    $"lifetime {s.lifetimeViews:N0}, quota {s.currentQuotaViews:N0}.");

                // ---- Lifetime milestones ----------------------------------------------
                foreach (var (_, total) in ViewMilestones.Table)
                {
                    if (prevLifetime < total && s.lifetimeViews >= total)
                    {
                        string locName = LocationNames.ReachedTotalViews(total);
                        long   locId   = LocationData.GetId(locName);
                        if (locId > 0)
                        {
                            Plugin.Logger.LogInfo(
                                $"[ViewsTracker] Lifetime milestone crossed: {locName}");
                            Plugin.SendCheck(locId);
                        }
                    }
                }

                // ---- Viral Sensation (per-quota 1M threshold) -------------------------
                if (!s.viralSensationFiredThisQuota
                    && prevQuota < ViewMilestones.ViralSensationThreshold
                    && s.currentQuotaViews >= ViewMilestones.ViralSensationThreshold)
                {
                    long locId = LocationData.GetId(LocationNames.ViralSensationAchieved);
                    if (locId > 0)
                    {
                        Plugin.Logger.LogInfo(
                            $"[ViewsTracker] Viral Sensation Achieved! " +
                            $"({s.currentQuotaViews:N0} views in this quota)");
                        Plugin.SendCheck(locId);
                        s.viralSensationFiredThisQuota = true;
                    }
                }

                APSave.Flush();

                // Always re-evaluate the win condition after the views counters
                // update.  The views_goal target may be met even when no milestone
                // table entry was crossed this upload (e.g. target falls between
                // two adjacent milestones), so we cannot rely solely on the
                // milestone-crossing path above to trigger the check.
                Plugin.Logger.LogDebug(
                    "[ViewsTracker] Triggering win condition check after video upload.");
                Plugin.connection.CheckWinCondition();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ViewsTracker] AddQuota postfix failed: {ex}");
            }
        }
    }

    // =========================================================================
    // SurfaceNetworkHandler.NewWeek(currentRun) — quota success
    // =========================================================================
    [HarmonyPatch(typeof(SurfaceNetworkHandler), nameof(SurfaceNetworkHandler.NewWeek))]
    internal static class NewWeekPatch
    {
        [HarmonyPostfix]
        static void Postfix(int currentRun)
        {
            try
            {
                ViewsTrackerHelpers.ResetQuotaCounter("quota passed");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ViewsTracker] NewWeek reset failed: {ex}");
            }

            if (!Plugin.connection.connected) return;
            if (!PhotonNetwork.IsMasterClient) return;
            if (currentRun < 1) return;

            try
            {
                string locName = LocationNames.MetQuotaPrefix + currentRun;
                long   locId   = LocationData.GetId(locName);
                if (locId > 0)
                {
                    Plugin.Logger.LogInfo($"[ViewsTracker] Quota {currentRun} passed → {locName}");
                    Plugin.SendCheck(locId);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ViewsTracker] NewWeek check failed: {ex}");
            }
        }
    }

    // =========================================================================
    // SurfaceNetworkHandler.RPC_QuotaFailed — reset per-quota counter
    // =========================================================================
    [HarmonyPatch(typeof(SurfaceNetworkHandler), "RPC_QuotaFailed")]
    internal static class QuotaFailedPatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            try
            {
                ViewsTrackerHelpers.ResetQuotaCounter("quota failed");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[ViewsTracker] QuotaFailed reset failed: {ex}");
            }
        }
    }

    // =========================================================================
    // Shared helpers
    // =========================================================================
    internal static class ViewsTrackerHelpers
    {
        public static void ResetQuotaCounter(string reason)
        {
            var s = APSave.saveData;
            if (s.currentQuotaViews == 0 && !s.viralSensationFiredThisQuota) return;

            Plugin.Logger.LogInfo(
                $"[ViewsTracker] Resetting quota counter ({reason}) — " +
                $"was {s.currentQuotaViews:N0} views, viral={s.viralSensationFiredThisQuota}.");
            s.currentQuotaViews = 0;
            s.viralSensationFiredThisQuota = false;
            APSave.Flush();
        }

        public static int TryGetCurrentDay()
        {
            var gameApiType = AccessTools.TypeByName("GameAPI");
            if (gameApiType != null)
            {
                var prop = AccessTools.Property(gameApiType, "CurrentDay");
                if (prop != null && prop.GetValue(null) is int d && d > 0) return d;
            }

            var snhType = AccessTools.TypeByName("SurfaceNetworkHandler");
            if (snhType != null)
            {
                var roomStatsProp = AccessTools.Property(snhType, "RoomStats");
                var roomStats = roomStatsProp?.GetValue(null);
                if (roomStats != null)
                {
                    var currentDayProp = AccessTools.Property(roomStats.GetType(), "CurrentDay");
                    if (currentDayProp != null && currentDayProp.GetValue(roomStats) is int d2 && d2 > 0)
                        return d2;
                }
            }
            return 0;
        }

        /// <summary>Reflectively call <c>BigNumbers.GetScoreToViews(float, int)</c>.
        /// Returns 0 on any failure so the tracker silently no-ops if the game
        /// renames the helper.</summary>
        public static int SafeGetScoreToViews(int score, int day)
        {
            try
            {
                var bigNumbersType = AccessTools.TypeByName("BigNumbers");
                if (bigNumbersType == null) return 0;
                var m = AccessTools.Method(bigNumbersType, "GetScoreToViews",
                    new[] { typeof(float), typeof(int) });
                if (m == null) return 0;
                var result = m.Invoke(null, new object[] { (float)score, day });
                return result is int i ? i : 0;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug($"[ViewsTracker] GetScoreToViews fallback: {ex.Message}");
                return 0;
            }
        }
    }
}
