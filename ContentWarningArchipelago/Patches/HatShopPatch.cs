// Patches/HatShopPatch.cs
//
// Four patches that integrate the hat shop with Archipelago.
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
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 3 — HatShopRestockAPPatch
//   Target : HatShop.Restock()   [public void, called by RPCA_StockShop on all clients]
//
//   When AP is active, skips the vanilla random selection and instead:
//   1. Separates hats into 'unchecked AP locations' and 'already-checked'.
//   2. Fills shop slots with unchecked hats first (ensuring the player always
//      sees locations they haven't bought yet).
//   3. Pads remaining slots with already-checked hats using a deterministic
//      shuffle based on savedSeed.
//   4. Replicates the price-randomisation logic using System.Random(seed).
//
// ─────────────────────────────────────────────────────────────────────────────
// PATCH 4 — HatShopRestockLabelPatch
//   Target : HatShop.RPCA_StockShop(int seed)   [public PunRPC, called on ALL clients]
//
//   After the shop is stocked (and Restock() has populated ihat on each slot),
//   performs an Archipelago Location Scout for all 5 hat location IDs currently
//   in the shop.  On scout completion, replaces each HatBuyInteractable.nameText
//   with the AP item name found at that location, so players can see what
//   Archipelago item they would receive by buying each hat.
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
                    // RoomStats not ready yet — suppress this early call entirely.
                    // SurfaceReadyHatStockPatch will re-trigger StockShop() once
                    // SurfaceNetworkHandler.InitSurface() has finished and RoomStats is confirmed non-null.
                    Plugin.Logger.LogWarning(
                        "[HatShopStockPatch] RoomStats not ready — suppressing early call. " +
                        "Deferred to SurfaceReadyHatStockPatch.");
                    return false; // skip original (do NOT send a DateTime-seeded RPC)
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
    // PATCH 1b — Re-stock hat shop once RoomStats is confirmed ready
    //
    // Postfix on SurfaceNetworkHandler.InitSurface (private void).
    //
    // Problem: HatShop.Start() calls StockShop() during Unity's Start phase,
    // which may fire BEFORE SurfaceNetworkHandler.InitSurface() has run and
    // created RoomStats.  HatShopStockPatch.Prefix now suppresses that early
    // call instead of falling back to the DateTime seed, but this means the
    // shop stays empty until something re-triggers stocking with a valid seed.
    //
    // Solution: after InitSurface() completes (guaranteeing RoomStats != null),
    // this postfix invokes HatShop.StockShop() via reflection.
    // HatShopStockPatch.Prefix will intercept that call and use
    // RoomStats.CurrentDay as the seed — the correct, deterministic in-game seed.
    //
    // If HatShop.Start() happened to run AFTER InitSurface (i.e., no early-call
    // problem existed this session), the shop will simply be re-stocked with the
    // same seed and the same result — harmless.
    // =========================================================================
    [HarmonyPatch]
    internal static class SurfaceReadyHatStockPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("SurfaceNetworkHandler");
            if (type == null)
            {
                Plugin.Logger.LogWarning("[SurfaceReadyHatStockPatch] Could not find 'SurfaceNetworkHandler'. Patch skipped.");
                return null;
            }

            // InitSurface is private void — still patchable by name.
            var method = AccessTools.Method(type, "InitSurface");
            if (method == null)
            {
                Plugin.Logger.LogWarning("[SurfaceReadyHatStockPatch] Could not find 'InitSurface' on SurfaceNetworkHandler. Patch skipped.");
                return null;
            }

            Plugin.Logger.LogInfo("[SurfaceReadyHatStockPatch] Patching SurfaceNetworkHandler.InitSurface (Postfix)");
            return method;
        }

        /// <summary>
        /// Postfix: once InitSurface() has run, RoomStats is guaranteed non-null.
        /// Re-trigger HatShop.StockShop() so the hat shop is always seeded with
        /// RoomStats.CurrentDay rather than a real-world DateTime.
        /// Only the master client calls StockShop (which broadcasts the RPC to all).
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!Plugin.connection.connected) return;
            if (!PhotonNetwork.IsMasterClient) return;

            // RoomStats should be set by now, but guard defensively.
            if (SurfaceNetworkHandler.RoomStats == null)
            {
                Plugin.Logger.LogWarning("[SurfaceReadyHatStockPatch] RoomStats still null after InitSurface — skipping deferred restock.");
                return;
            }

            // Locate the static HatShop.instance field.
            var hatShopType = AccessTools.TypeByName("HatShop");
            if (hatShopType == null)
            {
                Plugin.Logger.LogWarning("[SurfaceReadyHatStockPatch] Could not find type 'HatShop'.");
                return;
            }

            var instanceField = AccessTools.Field(hatShopType, "instance");
            if (instanceField == null)
            {
                Plugin.Logger.LogWarning("[SurfaceReadyHatStockPatch] Could not find static 'instance' field on HatShop.");
                return;
            }

            var hatShopInstance = instanceField.GetValue(null);
            if (hatShopInstance == null)
            {
                // HatShop hasn't run Awake yet — the normal Start() call will handle stocking
                // and by that point RoomStats will be valid (InitSurface already ran).
                Plugin.Logger.LogDebug("[SurfaceReadyHatStockPatch] HatShop.instance is null — deferring to HatShop.Start().");
                return;
            }

            // Invoke the private StockShop() method.  HatShopStockPatch.Prefix will
            // intercept this and seed the shop with RoomStats.CurrentDay.
            var stockShopMethod = AccessTools.Method(hatShopType, "StockShop");
            if (stockShopMethod == null)
            {
                Plugin.Logger.LogWarning("[SurfaceReadyHatStockPatch] Could not find 'StockShop' on HatShop.");
                return;
            }

            Plugin.Logger.LogInfo(
                $"[SurfaceReadyHatStockPatch] RoomStats ready (Day={SurfaceNetworkHandler.RoomStats.CurrentDay}). " +
                $"Triggering HatShop.StockShop() with in-game day seed.");

            stockShopMethod.Invoke(hatShopInstance, null);
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
                long locationId = HatShopRestockAPPatch.ResolveHatLocationId(hatIdx);

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
    }

    // =========================================================================
    // PATCH 3 — Custom restock: show unchecked AP hat locations first
    //
    // Prefix on HatShop.Restock() — when AP is active, replaces the vanilla
    // random selection with one that prioritises hats whose AP location hasn't
    // been checked yet (i.e., the player hasn't bought them in this run).
    //
    // The vanilla logic:
    //   UnityEngine.Random.InitState(savedSeed);
    //   List<Hat> randomNoDuplicates = HatDatabase.instance.hats.GetRandomNoDuplicates(count);
    //   hatBuyInteractables[i].LoadHat(hat.gameObject, price: Round(base * Rand(0.5–2) / 10) * 10);
    //
    // Our replacement:
    //   • Build unchecked / checked lists.
    //   • Shuffle both deterministically with System.Random(savedSeed).
    //   • Fill slots: unchecked first, pad with checked.
    //   • Compute prices with the same seeded RNG.
    // =========================================================================
    [HarmonyPatch]
    internal static class HatShopRestockAPPatch
    {
        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("HatShop");
            if (type == null) return null;

            var method = AccessTools.Method(type, "Restock");
            if (method == null)
            {
                Plugin.Logger.LogWarning("[HatShopRestockAPPatch] Could not find 'Restock' on HatShop. Patch skipped.");
                return null;
            }

            Plugin.Logger.LogInfo("[HatShopRestockAPPatch] Patching HatShop.Restock");
            return method;
        }

        [HarmonyPrefix]
        private static bool Prefix(object __instance)
        {
            if (!Plugin.connection.connected) return true; // vanilla path
            if (HatDatabase.instance == null) return true;

            try
            {
                // ── Read the private savedSeed field ─────────────────────────────────
                var savedSeedField = AccessTools.Field(__instance.GetType(), "savedSeed");
                if (savedSeedField == null)
                {
                    Plugin.Logger.LogWarning("[HatShopRestockAPPatch] Could not find 'savedSeed'. Allowing original.");
                    return true;
                }
                int seed = (int)savedSeedField.GetValue(__instance);

                // ── Read hatBuyInteractables list via reflection ───────────────────────
                // (We use __instance as object here; HatShop.hatBuyInteractables is public
                //  but the patch receives it as 'object' in dynamic-dispatch context.)
                var hbiField = AccessTools.Field(__instance.GetType(), "hatBuyInteractables");
                var hatBuyInteractablesList = hbiField?.GetValue(__instance) as List<HatBuyInteractable>;
                if (hatBuyInteractablesList == null) return true;

                int slotCount = hatBuyInteractablesList.Count;
                Hat[] allHats = HatDatabase.instance.hats;
                if (allHats == null || allHats.Length == 0) return true;

                // ── Separate unchecked and checked hat AP locations ────────────────────
                var uncheckedIndices = new List<int>();
                var checkedIndices   = new List<int>();

                for (int i = 0; i < allHats.Length; i++)
                {
                    long locId = ResolveHatLocationId(i);
                    bool isChecked = locId < 0 || APSave.IsLocationChecked(locId);
                    if (isChecked) checkedIndices.Add(i);
                    else           uncheckedIndices.Add(i);
                }

                // ── Shuffle both lists deterministically ──────────────────────────────
                var rng = new System.Random(seed);
                Shuffle(uncheckedIndices, rng);
                Shuffle(checkedIndices,   rng);

                // ── Build selection: unchecked first, pad with checked ─────────────────
                var selection = new List<int>(slotCount);
                selection.AddRange(uncheckedIndices.Take(slotCount));
                if (selection.Count < slotCount)
                    selection.AddRange(checkedIndices.Take(slotCount - selection.Count));

                // ── Load hats into shop slots ──────────────────────────────────────────
                for (int i = 0; i < slotCount && i < selection.Count; i++)
                {
                    Hat hat = allHats[selection[i]];
                    // Replicate vanilla price: base * Rand[0.5, 2.0], rounded to nearest 10.
                    double scale = rng.NextDouble() * 1.5 + 0.5; // [0.5, 2.0)
                    int price = Mathf.RoundToInt(hat.GetBasePrice() * (float)scale / 10f) * 10;
                    hatBuyInteractablesList[i].LoadHat(hat.gameObject, price);
                }

                Plugin.Logger.LogInfo(
                    $"[HatShopRestockAPPatch] Shop stocked — " +
                    $"{Math.Min(uncheckedIndices.Count, slotCount)} unchecked slot(s), " +
                    $"{Math.Max(0, slotCount - uncheckedIndices.Count)} checked slot(s).");

                return false; // skip vanilla Restock()
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[HatShopRestockAPPatch] Exception: {ex}. Allowing original.");
                return true;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the Archipelago location ID for a hat at the given database index.
        ///
        /// Strategy 1: build "Bought {Hat.displayName}" and look it up in LocationData.
        /// Strategy 2: fallback to the fixed offset BaseId + 600 + hatIdx.
        /// Returns -1 if neither strategy succeeds.
        /// </summary>
        internal static long ResolveHatLocationId(int hatIdx)
        {
            if (HatDatabase.instance != null
                && hatIdx >= 0
                && hatIdx < HatDatabase.instance.hats.Length)
            {
                Hat hat = HatDatabase.instance.hats[hatIdx];
                if (hat != null && !string.IsNullOrWhiteSpace(hat.displayName))
                {
                    long locId = LocationData.GetId("Bought " + hat.displayName);
                    if (locId >= 0) return locId;
                }
            }

            // Fixed-offset fallback (BaseId + 600 + hatIdx).
            long fallback = LocationData.BaseId + 600 + hatIdx;
            return LocationData.IdToName.ContainsKey(fallback) ? fallback : -1L;
        }

        private static void Shuffle<T>(List<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }
    }

    // =========================================================================
    // PATCH 4 — After stocking, scout AP locations and replace name labels
    //
    // Postfix on HatShop.RPCA_StockShop — runs on every client after Restock()
    // has populated the shop slots.  Performs an async Archipelago Location
    // Scout for the 5 displayed hat location IDs, then:
    //   • Replaces each HatBuyInteractable.nameText with the AP item name.
    //   • Enables TMP Best Fit / auto-sizing on nameText so long AP names
    //     don't overflow the sign label.
    //   • Caches the AP name per slot in ScoutedNames so the hover-tooltip
    //     patch (HatBuyHoverPatch) can substitute it into hoverText.
    // =========================================================================
    [HarmonyPatch]
    internal static class HatShopRestockLabelPatch
    {
        /// <summary>
        /// Maps each HatBuyInteractable to the Archipelago item name scouted
        /// for its current slot.  Populated by UpdateHatLabelsAsync; cleared
        /// at the start of every restock so stale entries never survive a
        /// shop rotation.  Read by HatBuyHoverPatch.
        /// </summary>
        internal static readonly Dictionary<HatBuyInteractable, string> ScoutedNames
            = new Dictionary<HatBuyInteractable, string>();

        static MethodBase? TargetMethod()
        {
            var type = AccessTools.TypeByName("HatShop");
            if (type == null)
            {
                Plugin.Logger.LogWarning("[HatShopRestockLabelPatch] Could not find 'HatShop'. Patch skipped.");
                return null;
            }

            var method = AccessTools.Method(type, "RPCA_StockShop");
            if (method == null)
            {
                Plugin.Logger.LogWarning("[HatShopRestockLabelPatch] Could not find 'RPCA_StockShop'. Patch skipped.");
                return null;
            }

            Plugin.Logger.LogInfo("[HatShopRestockLabelPatch] Patching HatShop.RPCA_StockShop (Postfix)");
            return method;
        }

        /// <summary>
        /// Postfix on RPCA_StockShop — runs on ALL clients after Restock() has populated
        /// the shop slots.
        ///
        /// HOST PRIORITY: Only the master client scouts AP locations.  After scouting,
        /// the master broadcasts the resolved item names to all clients via
        /// <c>RPCA_SyncArchipelagoLabels</c> on the HatShop's existing PhotonView.
        /// Non-master clients clear stale labels and wait for the RPC.
        /// </summary>
        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!Plugin.connection.connected) return;

            // Clear stale scouted names on every client so the previous shop
            // rotation's labels never bleed into the new one.
            ScoutedNames.Clear();

            // Only the master client scouts and broadcasts.
            if (!PhotonNetwork.IsMasterClient) return;
            if (Plugin.connection.session == null) return;

            // Retrieve the public hatBuyInteractables list.
            var hbiField = AccessTools.Field(__instance.GetType(), "hatBuyInteractables");
            var slots = hbiField?.GetValue(__instance) as List<HatBuyInteractable>;
            if (slots == null || slots.Count == 0) return;

            // Ensure HatShopAPSyncBehaviour is attached to this GameObject so Photon
            // can dispatch RPCA_SyncArchipelagoLabels to it via the HatShop's PhotonView.
            var hatShopMono = __instance as UnityEngine.MonoBehaviour;
            if (hatShopMono != null &&
                hatShopMono.gameObject.GetComponent<HatShopAPSyncBehaviour>() == null)
            {
                hatShopMono.gameObject.AddComponent<HatShopAPSyncBehaviour>();
                Plugin.Logger.LogInfo(
                    "[HatShopRestockLabelPatch] Attached HatShopAPSyncBehaviour to HatShop GameObject.");
            }

            // Fire-and-forget: scout on master, then broadcast labels to all clients.
            _ = UpdateHatLabelsAsync(slots, __instance);
        }

        /// <summary>
        /// Master-client-only: scouts AP locations for every occupied hat slot,
        /// builds a labels array (one entry per slot, empty string if unresolved),
        /// then broadcasts the array to ALL clients via the HatShop's PhotonView RPC
        /// <c>RPCA_SyncArchipelagoLabels</c>.  The RPC handler on each client applies
        /// the labels to the shop UI and populates <see cref="ScoutedNames"/>.
        /// </summary>
        private static async Task UpdateHatLabelsAsync(
            List<HatBuyInteractable> slots,
            object hatShopInstance)
        {
            try
            {
                // ── Build slot→locationId mapping ─────────────────────────────────────
                var locationIds = new List<long>();
                var slotLocations = new Dictionary<int, long>();

                for (int i = 0; i < slots.Count; i++)
                {
                    var hbi = slots[i];
                    if (hbi == null || hbi.IsEmpty) continue;

                    long locId = HatShopRestockAPPatch.ResolveHatLocationId(hbi.ihat.runtimeHatIndex);
                    if (locId < 0) continue;

                    locationIds.Add(locId);
                    slotLocations[i] = locId;
                }

                if (locationIds.Count == 0)
                {
                    Plugin.Logger.LogDebug(
                        "[HatShopRestockLabelPatch] No resolvable hat locations — labels not broadcast.");
                    return;
                }

                Plugin.Logger.LogInfo(
                    $"[HatShopRestockLabelPatch] Master scouting {locationIds.Count} hat location(s)…");

                // ── Scout locations (master client only) ───────────────────────────────
                var session = Plugin.connection.session;
                if (session == null) return;

                var scouted = await session.Locations.ScoutLocationsAsync(locationIds.ToArray());
                if (scouted == null) return;

                // ── Build labels array (one entry per slot, empty if not resolved) ─────
                var labels = new string[slots.Count];
                for (int i = 0; i < labels.Length; i++)
                {
                    if (!slotLocations.TryGetValue(i, out long locId)) { labels[i] = string.Empty; continue; }
                    if (!scouted.TryGetValue(locId, out var info))     { labels[i] = string.Empty; continue; }
                    labels[i] = info.ItemName ?? string.Empty;
                }

                // ── Broadcast to ALL clients (including master) via the HatShop's PhotonView ──
                // Photon dispatches RPCA_SyncArchipelagoLabels to every MonoBehaviour on the
                // same GameObject as the PhotonView — including HatShopAPSyncBehaviour.
                var viewField   = AccessTools.Field(hatShopInstance.GetType(), "view");
                var photonView  = viewField?.GetValue(hatShopInstance) as PhotonView;

                if (photonView != null && HatShopAPSyncBehaviour.Instance != null)
                {
                    Plugin.Logger.LogInfo(
                        $"[HatShopRestockLabelPatch] Broadcasting {labels.Length} AP label(s) to all clients.");
                    photonView.RPC("RPCA_SyncArchipelagoLabels", RpcTarget.All, (object)labels);
                }
                else
                {
                    // Fallback: apply labels locally only (e.g. solo / offline mode).
                    Plugin.Logger.LogWarning(
                        "[HatShopRestockLabelPatch] PhotonView or APSyncBehaviour unavailable — " +
                        "applying labels locally.");
                    HatShopAPSyncBehaviour.Instance?.RPCA_SyncArchipelagoLabels(labels);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[HatShopRestockLabelPatch] Label update failed: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // PATCH 5 — Hover tooltip: replace vanilla hat name with scouted AP name
    //
    // Postfix on HatBuyInteractable.get_hoverText.
    //
    // The vanilla getter returns a localized string that embeds ihat.GetName()
    // via a {hatName} replacement, e.g.:
    //   "Buy Balaclava"  /  "Can't afford Balaclava"  /  "Already own Balaclava"
    //
    // When AP is connected and UpdateHatLabelsAsync has populated ScoutedNames
    // for this slot, we swap ihat.GetName() for the AP item name so the player
    // sees the Archipelago item instead:
    //   "Buy Progressive Camera"  /  "Can't afford Progressive Camera"  / …
    // =========================================================================
    [HarmonyPatch(typeof(HatBuyInteractable), "get_hoverText")]
    internal static class HatBuyHoverPatch
    {
        [HarmonyPostfix]
        private static void Postfix(HatBuyInteractable __instance, ref string __result)
        {
            // Only override when AP is live and we have a scouted name for this slot.
            if (!Plugin.connection.connected) return;
            if (string.IsNullOrEmpty(__result)) return;
            if (!HatShopRestockLabelPatch.ScoutedNames.TryGetValue(__instance, out string apName)) return;

            // Replace the vanilla hat name substring with the AP item name.
            string hatName = __instance.ihat?.GetName() ?? string.Empty;
            if (!string.IsNullOrEmpty(hatName))
                __result = __result.Replace(hatName, apName);
        }
    }

    // =========================================================================
    // HatShopAPSyncBehaviour
    //
    // A lightweight MonoBehaviour attached at runtime to the HatShop's
    // GameObject (which already owns the game's PhotonView).  Because Photon
    // dispatches a PhotonView RPC to every MonoBehaviour on the same
    // GameObject, attaching this component is sufficient for the master client
    // to call:
    //
    //   hatShopPhotonView.RPC("RPCA_SyncArchipelagoLabels", RpcTarget.All, labels)
    //
    // and have it execute here on every connected client — without needing a
    // second PhotonView or any network-instantiated prefab.
    // =========================================================================

    /// <summary>
    /// Receives the master client's scouted AP item labels and applies them
    /// to the hat shop UI on every Photon client.
    /// </summary>
    internal class HatShopAPSyncBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Singleton reference so <see cref="HatShopRestockLabelPatch"/> can
        /// confirm the component exists before sending the RPC.
        /// </summary>
        internal static HatShopAPSyncBehaviour? Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
            Plugin.Logger.LogDebug("[HatShopAPSyncBehaviour] Attached to HatShop.");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Called on <b>all</b> clients (including the master) by the master client
        /// after it has scouted the hat shop's AP locations.
        ///
        /// Updates each occupied slot's name label with the corresponding AP item
        /// name and caches the mapping in <see cref="HatShopRestockLabelPatch.ScoutedNames"/>
        /// so the hover-tooltip patch (<see cref="HatBuyHoverPatch"/>) can use it.
        /// </summary>
        /// <param name="labels">
        /// One entry per hat shop slot in order.  Empty string means no AP item
        /// was resolved for that slot — the slot label is left unchanged.
        /// </param>
        [PunRPC]
        public void RPCA_SyncArchipelagoLabels(string[] labels)
        {
            Plugin.Logger.LogInfo(
                $"[HatShopAPSyncBehaviour] RPCA_SyncArchipelagoLabels received ({labels.Length} label(s)).");

            // ── Locate the HatShop instance via reflection ─────────────────────────
            var hatShopType   = AccessTools.TypeByName("HatShop");
            var instanceField = hatShopType != null ? AccessTools.Field(hatShopType, "instance") : null;
            var hatShop       = instanceField?.GetValue(null);

            if (hatShop == null)
            {
                Plugin.Logger.LogWarning("[HatShopAPSyncBehaviour] HatShop.instance is null — cannot apply labels.");
                return;
            }

            var hbiField = AccessTools.Field(hatShop.GetType(), "hatBuyInteractables");
            var slots    = hbiField?.GetValue(hatShop) as List<HatBuyInteractable>;

            if (slots == null)
            {
                Plugin.Logger.LogWarning("[HatShopAPSyncBehaviour] Could not retrieve hatBuyInteractables.");
                return;
            }

            // ── Clear and repopulate ScoutedNames ──────────────────────────────────
            HatShopRestockLabelPatch.ScoutedNames.Clear();

            for (int i = 0; i < slots.Count && i < labels.Length; i++)
            {
                string apItemName = labels[i];
                if (string.IsNullOrEmpty(apItemName)) continue;

                var hbi = slots[i];
                if (hbi == null || hbi.IsEmpty || hbi.nameText == null) continue;

                // ── Update the shop sign label ─────────────────────────────────────
                hbi.nameText.text = apItemName;

                // ── Enable Best Fit / Auto-Size so long AP names don't overflow ────
                float originalSize = hbi.nameText.fontSize;
                hbi.nameText.enableAutoSizing = true;
                hbi.nameText.fontSizeMin      = 1f;
                hbi.nameText.fontSizeMax      = Mathf.Max(10f, originalSize);

                // ── Cache for hover-tooltip ────────────────────────────────────────
                HatShopRestockLabelPatch.ScoutedNames[hbi] = apItemName;

                Plugin.Logger.LogInfo(
                    $"[HatShopAPSyncBehaviour] Slot {i} → '{apItemName}'");
            }
        }
    }
}
