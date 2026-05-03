// Patches/SponsorPatch.cs
//
// Fires "Completed Sponsorship N" location checks when a sponsorship
// (game-internal "NetworkDeal") transitions to the success state.
//
// Per issue #5 Q3 (apworld answer 3):
//   • Trigger is per-COMPLETION (state == success), not acceptance.
//   • A monotonic counter persists across run loss / restart.  Failing a run
//     mid-sponsorship doesn't reset the counter — the next completed
//     sponsorship after restart fires check N+1, not check 1 again.
//
// Hook strategy:
//   We patch NetworkDealBase's State property setter postfix.  When State
//   transitions to DEAL_STATE.success, we increment APSaveData.sponsorshipsCompleted
//   and fire the matching AP location.
//
//   The State setter is called from RoomStatsHolder.AddQuota (via NetworkDealBase
//   .ProgressInt setter, which auto-flips to success when GetProgress() >= 1f)
//   and from various RPC handlers in NetworkDealBoss.  Patching at the State
//   setter catches all paths — both progress-driven and direct state-set ones.
//
//   Master-client guard: AddQuota is master-only, but RPCA_HardSyncDeal /
//   RPCA_SyncDealProgress run on every client.  We guard with IsMasterClient
//   so only the host fires the AP check (one check per completion).
//
//   De-dupe: we track the last-seen state per deal instance.  The setter only
//   logs/fires on the unInited→progressing→success transition (specifically the
//   progressing→success edge), not on subsequent re-sets to success (HardSync
//   propagates the success state to non-masters; the master may also see its
//   own success state re-set from various code paths).

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using HarmonyLib;
using Photon.Pun;

namespace ContentWarningArchipelago.Patches
{
    [HarmonyPatch]
    internal static class NetworkDealStatePatch
    {
        // Per-deal cache of the last-observed State value.  ConditionalWeakTable
        // keys on the deal instance and lets the GC reclaim entries when the
        // deal goes out of scope (deals are short-lived per quota).
        private static readonly ConditionalWeakTable<object, object> _lastState
            = new ConditionalWeakTable<object, object>();

        // The DEAL_STATE.success enum value, resolved once via reflection so we
        // don't take a hard compile-time dependency on NetworkDealBase + its
        // nested enum.
        private static object? _successValue;

        static MethodBase? TargetMethod()
        {
            var dealType = AccessTools.TypeByName("NetworkDealBase");
            if (dealType == null)
            {
                Plugin.Logger.LogWarning(
                    "[SponsorPatch] NetworkDealBase type not found — sponsorship checks disabled.");
                return null;
            }

            var stateProp = AccessTools.Property(dealType, "State");
            if (stateProp == null || stateProp.GetSetMethod(nonPublic: true) == null)
            {
                Plugin.Logger.LogWarning(
                    "[SponsorPatch] NetworkDealBase.State setter not found — sponsorship checks disabled.");
                return null;
            }

            // Resolve DEAL_STATE.success (nested enum on NetworkDealBase).
            var enumType = dealType.GetNestedType("DEAL_STATE", BindingFlags.Public | BindingFlags.NonPublic);
            if (enumType != null)
            {
                try { _successValue = Enum.Parse(enumType, "success"); }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[SponsorPatch] Could not resolve DEAL_STATE.success: {ex.Message}");
                }
            }

            return stateProp.GetSetMethod(nonPublic: true);
        }

        [HarmonyPostfix]
        static void Postfix(object __instance, object value)
        {
            try
            {
                if (_successValue == null) return;
                if (!Equals(value, _successValue)) return;

                // De-dupe by instance.  Only fire on the first transition to success.
                if (_lastState.TryGetValue(__instance, out var last) && Equals(last, _successValue))
                    return;
                _lastState.Remove(__instance);
                _lastState.Add(__instance, _successValue);

                if (!Plugin.connection.connected) return;
                if (!PhotonNetwork.IsMasterClient) return;

                var s = APSave.saveData;
                s.sponsorshipsCompleted++;
                int n = s.sponsorshipsCompleted;

                if (n > 20)
                {
                    Plugin.Logger.LogInfo(
                        $"[SponsorPatch] Sponsorship #{n} completed — " +
                        $"beyond the 20-check ceiling, no AP check sent.");
                    APSave.Flush();
                    return;
                }

                string locName = LocationNames.CompletedSponsorshipPrefix + n;
                long   locId   = LocationData.GetId(locName);
                if (locId > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[SponsorPatch] Sponsorship #{n} completed → {locName}");
                    Plugin.SendCheck(locId);
                }

                APSave.Flush();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[SponsorPatch] State setter postfix failed: {ex}");
            }
        }
    }
}
