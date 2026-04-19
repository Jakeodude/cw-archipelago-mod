// Patches/HatShopPatch.cs
//
// Two patches that integrate the hat shop with Archipelago.
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 1 — HatShopStockPatch
//   Target : HatShop.StockShop()   [private void, called by master client only]
//
//   Vanilla seeds the daily shop with DateTime.Today.Date.GetHashCode(), which
//   ties the stock rotation to real-world time and Steam account persistence.
//
//   When AP is active we replace that seed with
//   SurfaceNetworkHandler.RoomStats.CurrentDay so that the shop stock changes
//   every time players sleep (i.e., every in-game day), regardless of wall clock.
//
//   Implementation: Prefix returns false (skips original) and replicates the
//   RPC call with the in-game-day seed via reflection on the private `view` field.
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 2 — HatBuyAPCheckPatch
//   Target : HatShop.RPCA_BuyHat(int hatBuyIndex, int buyerActorNumber)
//            [public PunRPC, broadcast to ALL clients]
//
//   WHY PREFIX (not Postfix):
//   At the very end of RPCA_BuyHat, hatBuyInteractable.ClearHat() is called,
//   which sets ihat = null.  A Postfix would see a null hat.  A Prefix runs
//   before ClearHat(), so we can safely read ihat.runtimeHatIndex.
//
//   WHAT WE DO:
//   • Mirror the game's own player-resolution logic to confirm the local player
//     is the buyer (so only one client sends the AP check per purchase).
//   • Read the hat name from HatDatabase and resolve it to an AP location ID
//     via LocationData ("Bought {displayName}").
//   • Fallback: if displayName lookup fails, try offset 600 + runtimeHatIndex
//     (valid for the standard 31-hat database, offsets 600–630).
//   • Call Plugin.SendCheck() to record and transmit the AP location check.
//   • (Hat session-unlock is handled separately by UnlockHatPatch intercepting
//     the MetaProgressionHandler.UnlockHat call made by RPCA_BuyHat itself.)
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Reflection;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // PATCH 1 — Replace DateTime shop seed with in-game day number
    // =========================================================================
    [HarmonyPatch]
    internal static class HatShopStockPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("HatShop");
            if (type == null)
            {
                Plugin.Logger.LogWarning("[HatShopStockPatch] Could not find type 'HatShop'. Patch skipped.");
                return null;
            }

            // StockShop is private void — still patchable via string name.
            var method = AccessTools.Method(type, "StockShop");
            if (method == null)
            {
                Plugin.Logger.LogWarning("[HatShopStockPatch] Could not find 'StockShop' on HatShop. Patch skipped.");
                return null;
            }

            Plugin.Logger.LogInfo($"[HatShopStockPatch] Patching HatShop.StockShop");
            return method;
        }

        /// <summary>
        /// Prefix: when AP is active and we are the master client, send the
        /// RPCA_StockShop RPC with <c>RoomStats.CurrentDay</c> as the seed
        /// instead of the vanilla <c>DateTime.Today.Date.GetHashCode()</c>.
        /// Returns <c>false</c> to skip the original method.
        /// </summary>
        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            if (!Plugin.connection.connected) return true;   // vanilla path
            if (!PhotonNetwork.IsMasterClient)  return true; // non-master: vanilla path

            try
            {
                // Resolve the seed: CurrentDay from RoomStats (in-game day counter).
                int seed;
                if (SurfaceNetworkHandler.RoomStats != null)
                {
                    seed = SurfaceNetworkHandler.RoomStats.CurrentDay;
                    Plugin.Logger.LogInfo($"[HatShopStockPatch] Seeding hat shop with in-game day {seed}.");
                }
                else
                {
                    // RoomStats not ready (shouldn't happen for StockShop, but be safe).
                    seed = DateTime.Today.Date.GetHashCode();
                    Plugin.Logger.LogWarning(
                        "[HatShopStockPatch] RoomStats not ready — falling back to DateTime seed.");
                }

                // Get the private PhotonView field `view` via reflection.
                var viewField = AccessTools.Field(__instance.GetType(), "view");
                if (viewField == null)
                {
                    Plugin.Logger.LogWarning("[HatShopStockPatch] Could not find 'view' field on HatShop. Allowing original.");
                    return true;
                }

                var photonView = viewField.GetValue(__instance) as PhotonView;
                if (photonView == null)
                {
                    Plugin.Logger.LogWarning("[HatShopStockPatch] PhotonView is null on HatShop. Allowing original.");
                    return true;
                }

                photonView.RPC("RPCA_StockShop", RpcTarget.All, seed);
                return false; // skip original DateTime-seeded RPC
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[HatShopStockPatch] Exception in Prefix: {ex}. Allowing original.");
                return true;
            }
        }
    }

    // =========================================================================
    // PATCH 2 — Fire AP location check on hat purchase
    // =========================================================================
    [HarmonyPatch(typeof(HatShop), "RPCA_BuyHat")]
    internal static class HatBuyAPCheckPatch
    {
        // Hat AP location offsets start at 600 in LocationData.
        // Offsets 600–630 cover all 31 vanilla hats (Balaclava … Savannah Hair).
        private const int HatLocationBaseOffset = 600;

        /// <summary>
        /// Prefix runs BEFORE hatBuyInteractable.ClearHat() nulls the ihat field,
        /// so we can safely read the runtime hat index here.
        /// Only sends the check for the local player who made the purchase.
        /// </summary>
        [HarmonyPrefix]
        private static void Prefix(HatShop __instance, int hatBuyIndex, int buyerActorNumber)
        {
            if (!Plugin.connection.connected) return;

            try
            {
                // ---- Validate hat slot ----
                if (hatBuyIndex < 0 || hatBuyIndex >= __instance.hatBuyInteractables.Count)
                {
                    Plugin.Logger.LogWarning($"[HatBuyAPCheckPatch] hatBuyIndex {hatBuyIndex} out of range.");
                    return;
                }

                HatBuyInteractable hbi = __instance.hatBuyInteractables[hatBuyIndex];
                if (hbi == null || hbi.ihat == null)
                {
                    Plugin.Logger.LogWarning("[HatBuyAPCheckPatch] Hat interactable or ihat is null.");
                    return;
                }

                // ---- Only the buyer sends the check ----
                // Mirror the game's own logic: TryGetPlayerFromOwnerID.
                if (!PlayerHandler.instance.TryGetPlayerFromOwnerID(buyerActorNumber, out var buyer))
                    return;

                if (Player.localPlayer != buyer)
                    return; // Another player's purchase — we don't send their check.

                // ---- Capture hat index while ihat is still alive ----
                int hatIdx = hbi.ihat.runtimeHatIndex;

                // ---- Resolve AP location name ----
                long locationId = ResolveHatLocationId(hatIdx);

                if (locationId < 0)
                {
                    Plugin.Logger.LogWarning(
                        $"[HatBuyAPCheckPatch] Could not resolve AP location for hat index {hatIdx}.");
                    return;
                }

                Plugin.Logger.LogInfo(
                    $"[HatBuyAPCheckPatch] Hat purchased (index={hatIdx}) → sending AP check " +
                    $"'{LocationData.GetName(locationId)}' ({locationId}).");

                Plugin.SendCheck(locationId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[HatBuyAPCheckPatch] Exception in Prefix: {ex}");
            }
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Resolves the AP location ID for the purchased hat.
        ///
        /// Strategy 1 (preferred): build "Bought {displayName}" from
        ///   HatDatabase.instance.hats[hatIdx].displayName and look it up
        ///   in <see cref="LocationData.NameToId"/>.
        ///
        /// Strategy 2 (fallback): use the fixed offset 600 + hatIdx.
        ///   This works if the HatDatabase array order matches the location
        ///   table order exactly (both contain 31 hats in the same sequence).
        ///   A warning is logged so mismatches are visible in the log.
        ///
        /// Returns -1 if neither strategy produces a valid location ID.
        /// </summary>
        private static long ResolveHatLocationId(int hatIdx)
        {
            // Strategy 1: displayName lookup
            if (HatDatabase.instance != null
                && hatIdx >= 0
                && hatIdx < HatDatabase.instance.hats.Length)
            {
                Hat hat = HatDatabase.instance.hats[hatIdx];
                if (hat != null && !string.IsNullOrWhiteSpace(hat.displayName))
                {
                    string locName = "Bought " + hat.displayName;
                    long locId = LocationData.GetId(locName);
                    if (locId >= 0)
                    {
                        Plugin.Logger.LogDebug(
                            $"[HatBuyAPCheckPatch] Resolved via displayName: '{locName}' → {locId}");
                        return locId;
                    }

                    Plugin.Logger.LogDebug(
                        $"[HatBuyAPCheckPatch] displayName lookup missed for '{locName}'. Trying index fallback.");
                }
            }

            // Strategy 2: fixed offset fallback
            long fallbackId = LocationData.BaseId + HatLocationBaseOffset + hatIdx;
            if (LocationData.IdToName.ContainsKey(fallbackId))
            {
                Plugin.Logger.LogInfo(
                    $"[HatBuyAPCheckPatch] Using index-based fallback: hatIdx={hatIdx} → {fallbackId} " +
                    $"('{LocationData.GetName(fallbackId)}').");
                return fallbackId;
            }

            return -1L;
        }
    }
}
