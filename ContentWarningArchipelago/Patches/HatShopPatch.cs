// Patches/HatShopPatch.cs
//
// Hat shop overhaul for the Archipelago mod.
//
// CHANGES vs vanilla HatShop:
//
// 1. FIXED STOCK (HatShopFixedSeedPatch)
//    Replaces the vanilla date-based seed so the shop never cycles daily.
//    All clients in the same AP session see the same hat selection forever.
//    A prefix on RPCA_StockShop replaces the incoming seed with a hash of
//    the AP server's seed string, giving a stable, session-consistent RNG.
//
// 2. AP ITEM LABELS (HatShopAPLabelPatch + HatShopAPSyncBehaviour)
//    After Restock(), the master client scouts each visible hat's AP location
//    via Plugin.connection.ScoutLocationsAsync() to discover which AP item is
//    hidden behind it.  Each slot's nameText is then updated to:
//        "<HatName> [<APItemName>]"
//    e.g. "Beanie [Progressive Camera]"
//
//    The master then broadcasts the label array to all other clients via
//    the RPCA_SyncArchipelagoLabels PunRPC on the HatShopAPSyncBehaviour
//    component attached to HatShop.instance.gameObject.
//
//    For late joiners, LateJoinSyncPatch.SyncHatLabelsToPlayer() sends a
//    targeted RPC to just the new player using the cached ScoutedNames dict.
//
// 3. STAGE PRICING (HatShopRestockPatch — unchanged from before)
//    Replaces vanilla random pricing with fixed AP-stage prices:
//      Early → 500 MC   Mid → 1 000 MC   Late → 2 000 MC

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using HarmonyLib;
using Photon.Pun;
using pworld.Scripts.Extensions;
using TMPro;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // 1. PER-DAY STOCK — prefix on HatShop.RPCA_StockShop
    //
    // Vanilla seed is DateTime.Today.Date.GetHashCode() — same hats for the
    // entire UTC day regardless of in-game progression.  We replace it with
    // (apSeed + ":" + currentDay).GetHashCode() so the pool advances each
    // in-game day but is still deterministic across all clients in the same
    // AP session.  HatShop already calls StockShop on every dive transition
    // (Start, StartGameAction, ReturnToSurfaceAction), so a per-day seed
    // change is enough to refresh the displayed pool.  Issue #4.
    // =========================================================================

    [HarmonyPatch(typeof(HatShop), "RPCA_StockShop")]
    internal static class HatShopFixedSeedPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref int seed)
        {
            if (!Plugin.connection.connected) return;

            string? apSeed = Plugin.connection.session?.RoomState.Seed;
            if (string.IsNullOrEmpty(apSeed)) return;

            int day = TryGetCurrentDay();
            seed = $"{apSeed}:{day}".GetHashCode();
            Plugin.Logger.LogInfo(
                $"[HatShopFixedSeedPatch] Hat shop seed overridden " +
                $"(apSeed + day {day}): {seed}");
        }

        /// <summary>
        /// Reads the current in-game day.  Returns 0 if unresolvable (e.g. on
        /// the very first StockShop call from HatShop.Start before any dive).
        /// Falls back through GameAPI.CurrentDay → SurfaceNetworkHandler.RoomStats.CurrentDay.
        /// </summary>
        private static int TryGetCurrentDay()
        {
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
    // 1b. FILTERED RESTOCK — prefix-skip on HatShop.Restock
    //
    // Replaces the vanilla Restock body with one that filters out hats whose
    // AP "Bought X" location has already been checked, so a purchased hat
    // permanently leaves the pool.  When fewer than the slot count remain,
    // the unfilled slots are left empty (ihat = null) per issue #4.
    //
    // Returns false → original Restock is skipped.  All Postfixes on Restock
    // (HatShopAPLabelPatch.Postfix and HatShopRestockPatch.Postfix) still run
    // because Harmony postfixes execute even when the prefix skips the
    // original — they are guarded by `slot.IsEmpty` so unfilled slots are
    // ignored gracefully.
    //
    // RNG determinism: every connected client runs this prefix with the
    // identical seed (set in RPCA_StockShop) and identical filter set
    // (AllLocationsChecked from the shared AP server), so all clients pick
    // the same hats for the same slots.  Disconnected clients fall through
    // to vanilla and may show extra hats that master sees as empty — those
    // purchases are rejected by the master via the existing IsEmpty guard
    // in RPCM_TryBuyHat.  See PR #N for full discussion.
    // =========================================================================

    [HarmonyPatch(typeof(HatShop), nameof(HatShop.Restock))]
    internal static class HatShopFilterRestockPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(HatShop __instance)
        {
            // No AP connection → defer to vanilla Restock.
            if (!Plugin.connection.connected) return true;
            if (HatDatabase.instance == null || HatDatabase.instance.hats == null) return true;

            // Read the saved seed that RPCA_StockShop just stored.
            var seedField = AccessTools.Field(typeof(HatShop), "savedSeed");
            if (seedField == null) return true;
            int savedSeed = (int)(seedField.GetValue(__instance) ?? 0);

            var rngState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(savedSeed);

            try
            {
                var checkedLocs = Plugin.connection.session?.Locations.AllLocationsChecked;

                // Build pool of hats whose AP location is not yet checked.
                // Hats with no AP mapping (LocationData.GetId < 0) stay in the
                // pool so the shop still functions for vanilla-only hats.
                var available = new List<Hat>();
                foreach (var hat in HatDatabase.instance.hats)
                {
                    if (hat == null) continue;
                    if (IsHatLocationChecked(hat, checkedLocs)) continue;
                    available.Add(hat);
                }

                int slotCount = __instance.hatBuyInteractables.Count;
                int pickCount = Math.Min(slotCount, available.Count);

                List<Hat> picked = pickCount > 0
                    ? available.GetRandomNoDuplicates(pickCount)
                    : new List<Hat>();

                for (int i = 0; i < slotCount; i++)
                {
                    var slot = __instance.hatBuyInteractables[i];
                    if (slot == null) continue;

                    if (i < picked.Count)
                    {
                        Hat hat = picked[i];
                        // Vanilla random price formula — postfix overwrites with
                        // tier price, but we match vanilla's RNG draws so the
                        // seeded state advances identically across clients.
                        int basePrice = Mathf.RoundToInt(
                            (float)hat.GetBasePrice() * UnityEngine.Random.Range(0.5f, 2f) / 10f) * 10;
                        slot.LoadHat(hat.gameObject, basePrice);
                    }
                    else
                    {
                        // Pool exhausted (late-game) — leave slot empty.
                        slot.ClearHat();
                        if (slot.nameText  != null) slot.nameText.text  = string.Empty;
                        if (slot.priceText != null) slot.priceText.text = string.Empty;
                    }
                }

                Plugin.Logger.LogInfo(
                    $"[HatShopFilterRestockPatch] Filtered restock: " +
                    $"{pickCount}/{slotCount} slots filled, " +
                    $"{available.Count} unbought hats in pool.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[HatShopFilterRestockPatch] Exception: {ex}");
            }
            finally
            {
                UnityEngine.Random.state = rngState;
            }

            return false; // skip vanilla Restock body
        }

        /// <summary>
        /// True when the hat's AP "Bought X" location is in the checked set.
        /// False (= keep in pool) for hats with no AP mapping or unknown to the
        /// AP location table.
        /// </summary>
        private static bool IsHatLocationChecked(Hat hat, IReadOnlyCollection<long>? checkedLocs)
        {
            if (checkedLocs == null || checkedLocs.Count == 0) return false;

            string? locName = null;
            if (!string.IsNullOrEmpty(hat.displayName) &&
                HatShopAPLabelPatch.HatNameToLocation.TryGetValue(hat.displayName, out var loc1))
                locName = loc1;
            else
            {
                string localName = hat.GetName();
                if (!string.IsNullOrEmpty(localName) &&
                    HatShopAPLabelPatch.HatNameToLocation.TryGetValue(localName, out var loc2))
                    locName = loc2;
            }

            if (string.IsNullOrEmpty(locName)) return false;

            long locId = LocationData.GetId(locName);
            if (locId < 0) return false;

            return checkedLocs.Contains(locId);
        }
    }

    // =========================================================================
    // 2. AP ITEM LABELS — postfix on HatShop.Restock
    //
    // Runs AFTER the shop has stocked its slots.  Attaches (or reuses) a
    // HatShopAPSyncBehaviour on the HatShop's GameObject and starts the
    // async scout coroutine there.
    // =========================================================================

    [HarmonyPatch(typeof(HatShop), nameof(HatShop.Restock))]
    internal static class HatShopAPLabelPatch
    {
        // ------------------------------------------------------------------
        // Hat display-name → AP "Bought X" location name
        // ------------------------------------------------------------------
        internal static readonly Dictionary<string, string> HatNameToLocation =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Early ──
            { "Beanie",          "Bought Beanie"        },
            { "Bucket Hat",      "Bought Bucket Hat"    },
            { "Floppy Hat",      "Bought Floppy Hat"    },
            { "Homburg",         "Bought Homburg"       },
            { "Bowler Hat",      "Bought Bowler Hat"    },
            { "Cap",             "Bought Cap"           },
            { "News Cap",        "Bought News Cap"      },
            { "Newsboy Cap",     "Bought News Cap"      },  // in-game variant
            { "Sports Helmet",   "Bought Sports Helmet" },
            { "Hard Hat",        "Bought Hard Hat"      },
            // ── Mid ──
            { "Chefs Hat",       "Bought Chefs Hat"     },
            { "Chef's Hat",      "Bought Chefs Hat"     },
            { "Propeller Hat",   "Bought Propeller Hat" },
            { "Cowboy Hat",      "Bought Cowboy Hat"    },
            { "Horns",           "Bought Horns"         },
            { "Hotdog Hat",      "Bought Hotdog Hat"    },
            { "Hot Dog Hat",     "Bought Hotdog Hat"    },
            { "Milk Hat",        "Bought Milk Hat"      },
            { "Pirate Hat",      "Bought Pirate Hat"    },
            { "Top Hat",         "Bought Top Hat"       },
            { "Party Hat",       "Bought Party Hat"     },
            { "Ushanka",         "Bought Ushanka"       },
            // ── Late ──
            { "Balaclava",       "Bought Balaclava"     },
            { "Cat Ears",        "Bought Cat Ears"      },
            { "Curly Hair",      "Bought Curly Hair"    },
            { "Clown Hair",      "Bought Clown Hair"    },
            { "Crown",           "Bought Crown"         },
            { "Halo",            "Bought Halo"          },
            { "Jesters Hat",     "Bought Jesters Hat"   },
            { "Jester's Hat",    "Bought Jesters Hat"   },
            { "Ghost Hat",       "Bought Ghost Hat"     },
            { "Tooop Hat",       "Bought Tooop Hat"     },
            { "Shroom Hat",      "Bought Shroom Hat"    },
            { "Witch Hat",       "Bought Witch Hat"     },
            { "Savannah Hair",   "Bought Savannah Hair" },
        };

        [HarmonyPostfix]
        private static void Postfix(HatShop __instance)
        {
            if (!Plugin.connection.connected) return;
            // Only the master client scouts; non-master clients receive labels
            // via RPCA_SyncArchipelagoLabels broadcast from the master.
            if (!PhotonNetwork.IsMasterClient) return;

            // Attach (or reuse) the broadcaster MonoBehaviour.
            var broadcaster = __instance.GetComponent<HatShopAPSyncBehaviour>()
                           ?? __instance.gameObject.AddComponent<HatShopAPSyncBehaviour>();

            // Start the scout + label coroutine.
            broadcaster.LaunchScout(__instance);
        }
    }

    // =========================================================================
    // HatShopRestockLabelPatch — static cache (ScoutedNames)
    //
    // Referenced by LateJoinSyncPatch.SyncHatLabelsToPlayer.
    // Populated by HatShopAPSyncBehaviour.ScoutAndLabelCoroutine.
    // =========================================================================

    internal static class HatShopRestockLabelPatch
    {
        /// <summary>
        /// Maps each hat-shop slot → the AP label text currently shown
        /// (e.g. "Beanie [Progressive Camera]").
        /// Cleared and repopulated each time the shop is scouted.
        /// </summary>
        internal static readonly Dictionary<HatBuyInteractable, string> ScoutedNames =
            new Dictionary<HatBuyInteractable, string>();

        /// <summary>
        /// Maps each hat-shop slot → just the AP item name (e.g. "Progressive Camera"),
        /// without the hat-name prefix or brackets.
        /// Consumed by <see cref="HatBuyInteractableHoverTextPatch"/> to append AP
        /// context to the in-world hover tooltip.
        /// Cleared and repopulated alongside <see cref="ScoutedNames"/>.
        /// </summary>
        internal static readonly Dictionary<HatBuyInteractable, string> ScoutedAPItemNames =
            new Dictionary<HatBuyInteractable, string>();
    }

    // =========================================================================
    // HatShopAPSyncBehaviour — MonoBehaviour on HatShop.instance.gameObject
    //
    // Responsibilities:
    //   • Runs the async scout coroutine and applies labels locally.
    //   • Broadcasts labels to all other connected clients after scout.
    //   • Handles RPCA_SyncArchipelagoLabels PunRPC for non-master clients
    //     (and for late joiners via LateJoinSyncPatch).
    //   • Exposes Instance so LateJoinSyncPatch can guard on shop readiness.
    // =========================================================================

    internal class HatShopAPSyncBehaviour : MonoBehaviour
    {
        /// <summary>
        /// Non-null once the shop has been scouted at least once this session.
        /// LateJoinSyncPatch.SyncHatLabelsToPlayer guards on this being non-null
        /// before attempting the targeted label sync.
        /// </summary>
        internal static HatShopAPSyncBehaviour? Instance { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------
        // Entry point called by HatShopAPLabelPatch.Postfix (master only)
        // ------------------------------------------------------------------

        internal void LaunchScout(HatShop hatShop)
        {
            StopAllCoroutines();
            StartCoroutine(ScoutAndLabelCoroutine(hatShop));
        }

        // ------------------------------------------------------------------
        // Coroutine: scout → label → broadcast
        // ------------------------------------------------------------------

        private IEnumerator ScoutAndLabelCoroutine(HatShop hatShop)
        {
            // ── Step 1: map slots to AP location IDs ────────────────────────
            var slotToLocId  = new Dictionary<HatBuyInteractable, long>();
            var locIdsToScout = new List<long>();

            foreach (var slot in hatShop.hatBuyInteractables)
            {
                if (slot == null || slot.IsEmpty || slot.ihat == null) continue;

                string? locationName = ResolveLocationName(slot.ihat);
                if (locationName == null) continue;

                long locId = LocationData.GetId(locationName);
                if (locId < 0)
                {
                    Plugin.Logger.LogDebug(
                        $"[HatShopAPSyncBehaviour] '{locationName}' not in LocationData table.");
                    continue;
                }

                slotToLocId[slot] = locId;
                locIdsToScout.Add(locId);
            }

            if (locIdsToScout.Count == 0)
            {
                Plugin.Logger.LogDebug(
                    "[HatShopAPSyncBehaviour] No valid hat AP locations to scout.");
                yield break;
            }

            Plugin.Logger.LogInfo(
                $"[HatShopAPSyncBehaviour] Scouting {locIdsToScout.Count} hat location(s)…");

            // ── Step 2: async scout — yield until task completes ────────────
            var scoutTask = Plugin.connection.ScoutLocationsAsync(locIdsToScout);

            while (!scoutTask.IsCompleted)
                yield return null;

            Dictionary<long, string> scoutResult;
            try
            {
                scoutResult = scoutTask.Result ?? new Dictionary<long, string>();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[HatShopAPSyncBehaviour] Scout task threw: {ex.Message}");
                yield break;
            }

            // ── Step 3: apply labels locally + populate ScoutedNames ────────
            HatShopRestockLabelPatch.ScoutedNames.Clear();
            HatShopRestockLabelPatch.ScoutedAPItemNames.Clear();

            var labelsForBroadcast = new string[hatShop.hatBuyInteractables.Count];

            for (int i = 0; i < hatShop.hatBuyInteractables.Count; i++)
            {
                var slot = hatShop.hatBuyInteractables[i];
                labelsForBroadcast[i] = string.Empty;

                if (slot == null || slot.IsEmpty || slot.ihat == null) continue;
                if (!slotToLocId.TryGetValue(slot, out long locId)) continue;
                if (!scoutResult.TryGetValue(locId, out string? itemName)) continue;
                if (string.IsNullOrEmpty(itemName)) continue;

                string hatDisplay = slot.ihat.GetName();
                string labelText  = $"{hatDisplay} [{itemName}]";

                if (slot.nameText != null)
                {
                    slot.nameText.text = labelText;
                    ApplyAutoSizing(slot.nameText);
                }

                HatShopRestockLabelPatch.ScoutedNames[slot]        = labelText;
                HatShopRestockLabelPatch.ScoutedAPItemNames[slot]  = itemName;
                labelsForBroadcast[i] = labelText;

                Plugin.Logger.LogInfo(
                    $"[HatShopAPSyncBehaviour] Labelled slot {i}: '{hatDisplay}' → '{itemName}'");
            }

            Plugin.Logger.LogInfo(
                $"[HatShopAPSyncBehaviour] Applied {HatShopRestockLabelPatch.ScoutedNames.Count} label(s).");

            // ── Step 4: broadcast to all other clients ───────────────────────
            // Non-master clients rely on this RPC to display AP labels.
            // The RPC lands on RPCA_SyncArchipelagoLabels below.
            if (PhotonNetwork.CurrentRoom != null && PhotonNetwork.CurrentRoom.PlayerCount > 1)
            {
                var pv = hatShop.GetComponent<PhotonView>();
                if (pv != null)
                {
                    pv.RPC("RPCA_SyncArchipelagoLabels", RpcTarget.Others,
                           (object)labelsForBroadcast);
                    Plugin.Logger.LogInfo(
                        "[HatShopAPSyncBehaviour] Broadcast hat labels to other clients.");
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        "[HatShopAPSyncBehaviour] HatShop has no PhotonView — cannot broadcast labels.");
                }
            }
        }

        // ------------------------------------------------------------------
        // PunRPC handler — receives label array on non-master clients
        // Also used for late-joiner targeted sync via LateJoinSyncPatch.
        // ------------------------------------------------------------------

        [PunRPC]
        public void RPCA_SyncArchipelagoLabels(string[] labels)
        {
            if (HatShop.instance == null)
            {
                Plugin.Logger.LogWarning(
                    "[HatShopAPSyncBehaviour] RPCA_SyncArchipelagoLabels: HatShop.instance is null.");
                return;
            }

            var slots = HatShop.instance.hatBuyInteractables;

            for (int i = 0; i < labels.Length && i < slots.Count; i++)
            {
                string label = labels[i];
                if (string.IsNullOrEmpty(label)) continue;

                var slot = slots[i];
                if (slot == null || slot.IsEmpty || slot.ihat == null) continue;

                if (slot.nameText != null)
                {
                    slot.nameText.text = label;
                    ApplyAutoSizing(slot.nameText);
                }

                // Also update ScoutedNames so LateJoinSyncPatch has accurate data.
                HatShopRestockLabelPatch.ScoutedNames[slot] = label;

                // Parse "<HatName> [<APItemName>]" → cache the AP item name so the
                // HatBuyInteractable.hoverText Postfix can append it to the tooltip
                // on non-master and late-joining clients.
                int openBracket  = label.LastIndexOf('[');
                int closeBracket = label.LastIndexOf(']');
                if (openBracket >= 0 && closeBracket > openBracket)
                {
                    string apItemName = label.Substring(openBracket + 1, closeBracket - openBracket - 1);
                    if (!string.IsNullOrEmpty(apItemName))
                        HatShopRestockLabelPatch.ScoutedAPItemNames[slot] = apItemName;
                }
            }

            Plugin.Logger.LogInfo(
                $"[HatShopAPSyncBehaviour] RPCA_SyncArchipelagoLabels: applied {labels.Length} label(s).");
        }

        // (ApplyAutoSizing helper is defined below)

        // ------------------------------------------------------------------
        // UI helper — auto-resize TMP so long AP item names never clip
        // ------------------------------------------------------------------

        /// <summary>
        /// Enables TMP auto-sizing on a hat slot's name label so that long
        /// strings like "Beanie [Progressive Camera]" shrink to fit the
        /// physical board space rather than overflowing or being clipped.
        /// </summary>
        private static void ApplyAutoSizing(TextMeshPro tmp)
        {
            if (tmp == null) return;
            tmp.enableWordWrapping = true;
            tmp.enableAutoSizing   = true;
            // fontSizeMax should be at least the current configured size.
            tmp.fontSizeMax = Mathf.Max(tmp.fontSize, tmp.fontSizeMax > 0f ? tmp.fontSizeMax : tmp.fontSize);
            // Allow drastic shrinkage so long AP item names ("Progressive Camera",
            // "Progressive Battery") fit the physical board space (issue #3).
            tmp.fontSizeMin  = 8f;
            // Fail-safe: clip anything that still won't fit at the minimum size
            // rather than spilling outside the UI bounding box.
            tmp.overflowMode = TextOverflowModes.Truncate;
        }

        // ------------------------------------------------------------------
        // Name resolution helper
        // ------------------------------------------------------------------

        private static string? ResolveLocationName(Hat hat)
        {
            // Prefer prefab displayName; fall back to localized GetName().
            if (!string.IsNullOrEmpty(hat.displayName) &&
                HatShopAPLabelPatch.HatNameToLocation.TryGetValue(hat.displayName, out var loc1))
                return loc1;

            string localName = hat.GetName();
            if (!string.IsNullOrEmpty(localName) &&
                HatShopAPLabelPatch.HatNameToLocation.TryGetValue(localName, out var loc2))
                return loc2;

            Plugin.Logger.LogDebug(
                $"[HatShopAPSyncBehaviour] No location mapping for hat " +
                $"displayName='{hat.displayName}' / GetName()='{hat.GetName()}'.");
            return null;
        }
    }

    // =========================================================================
    // 2b. HAT PURCHASE AP CHECK — prefix on HatShop.RPCA_BuyHat
    //
    // Fires the "Bought X" AP location check when a hat is purchased.
    // Must be a Prefix (not Postfix) because hatBuyInteractable.ClearHat()
    // destroys slot.ihat at the end of RPCA_BuyHat; we need ihat still alive.
    // Only the master client sends the check (RPCA_BuyHat is RpcTarget.All).
    // =========================================================================

    [HarmonyPatch(typeof(HatShop), "RPCA_BuyHat")]
    internal static class HatShopBuyAPCheckPatch
    {
        [HarmonyPrefix]
        private static void Prefix(HatShop __instance, int hatBuyIndex)
        {
            if (!Plugin.connection.connected) return;
            if (!PhotonNetwork.IsMasterClient) return;

            try
            {
                if (hatBuyIndex < 0 || hatBuyIndex >= __instance.hatBuyInteractables.Count) return;

                var slot = __instance.hatBuyInteractables[hatBuyIndex];
                if (slot == null || slot.IsEmpty || slot.ihat == null) return;

                // Resolve AP location name using the same priority as the label patch.
                string? locationName = null;

                if (!string.IsNullOrEmpty(slot.ihat.displayName) &&
                    HatShopAPLabelPatch.HatNameToLocation.TryGetValue(slot.ihat.displayName, out var loc1))
                    locationName = loc1;
                else
                {
                    string localName = slot.ihat.GetName();
                    if (!string.IsNullOrEmpty(localName) &&
                        HatShopAPLabelPatch.HatNameToLocation.TryGetValue(localName, out var loc2))
                        locationName = loc2;
                }

                // Fallback: auto-compute from whichever name is available.
                if (locationName == null)
                {
                    string fallbackName = !string.IsNullOrEmpty(slot.ihat.displayName)
                        ? slot.ihat.displayName
                        : slot.ihat.GetName();
                    if (!string.IsNullOrEmpty(fallbackName))
                        locationName = "Bought " + fallbackName;
                }

                if (string.IsNullOrEmpty(locationName)) return;

                long locId = LocationData.GetId(locationName);
                if (locId < 0)
                {
                    Plugin.Logger.LogDebug(
                        $"[HatShopBuyAPCheckPatch] '{locationName}' not in AP location table.");
                    return;
                }

                Plugin.Logger.LogInfo(
                    $"[HatShopBuyAPCheckPatch] Hat purchase → sending check: {locationName}");
                Plugin.SendCheck(locId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HatShopBuyAPCheckPatch] Exception: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // 3. STAGE PRICING — postfix on HatShop.Restock (unchanged from before)
    //
    // Overwrites the vanilla random price with a fixed AP-stage price after
    // the shop has been stocked.  Runs on ALL clients since Restock() is
    // called via RPCA_StockShop (RpcTarget.All).
    // =========================================================================

    [HarmonyPatch(typeof(HatShop), nameof(HatShop.Restock))]
    internal static class HatShopRestockPatch
    {
        private static readonly Dictionary<string, int> HatStagePrices =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Early hats — 500 MC ───────────────────────────────────────────
            { "Beanie",          500 },
            { "Bucket Hat",      500 },
            { "Floppy Hat",      500 },
            { "Homburg",         500 },
            { "Bowler Hat",      500 },
            { "Cap",             500 },
            { "News Cap",        500 },
            { "Newsboy Cap",     500 },
            { "Sports Helmet",   500 },
            { "Hard Hat",        500 },

            // ── Mid hats — 1,000 MC ───────────────────────────────────────────
            { "Chefs Hat",      1000 },
            { "Chef's Hat",     1000 },
            { "Propeller Hat",  1000 },
            { "Cowboy Hat",     1000 },
            { "Horns",          1000 },
            { "Hotdog Hat",     1000 },
            { "Hot Dog Hat",    1000 },
            { "Milk Hat",       1000 },
            { "Pirate Hat",     1000 },
            { "Top Hat",        1000 },
            { "Party Hat",      1000 },
            { "Ushanka",        1000 },

            // ── Late hats — 2,000 MC ──────────────────────────────────────────
            { "Balaclava",      2000 },
            { "Cat Ears",       2000 },
            { "Curly Hair",     2000 },
            { "Clown Hair",     2000 },
            { "Crown",          2000 },
            { "Halo",           2000 },
            { "Jesters Hat",    2000 },
            { "Jester's Hat",   2000 },
            { "Ghost Hat",      2000 },
            { "Tooop Hat",      2000 },
            { "Shroom Hat",     2000 },
            { "Witch Hat",      2000 },
            { "Savannah Hair",  2000 },
        };

        private static int RarityFallbackPrice(RARITY rarity)
        {
            switch (rarity)
            {
                case RARITY.common:
                case RARITY.uncommon:
                    return 500;
                case RARITY.rare:
                case RARITY.epic:
                    return 1000;
                case RARITY.legendary:
                case RARITY.mythic:
                    return 2000;
                default:
                    return 500;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(HatShop __instance)
        {
            foreach (HatBuyInteractable slot in __instance.hatBuyInteractables)
            {
                if (slot == null || slot.IsEmpty || slot.ihat == null)
                    continue;

                int price;
                bool fromTable = true;

                if (!HatStagePrices.TryGetValue(slot.ihat.displayName, out price))
                {
                    string localName = slot.ihat.GetName();
                    if (!HatStagePrices.TryGetValue(localName, out price))
                    {
                        price     = RarityFallbackPrice(slot.ihat.rarity);
                        fromTable = false;
                        Plugin.Logger.LogDebug(
                            $"[HatShopRestockPatch] No stage mapping for " +
                            $"'{slot.ihat.displayName}' / '{localName}' " +
                            $"(rarity: {slot.ihat.rarity}) — fallback {price} MC.");
                    }
                }

                slot.ihat.priceToday = price;
                slot.priceText.text  = price + " MC";

                Plugin.Logger.LogDebug(
                    $"[HatShopRestockPatch] '{slot.ihat.GetName()}' → " +
                    $"{price} MC ({(fromTable ? "table" : "rarity fallback")})");
            }
        }
    }

    // =========================================================================
    // 4. HOVER TOOLTIP — postfix on HatBuyInteractable.hoverText getter
    //
    // Vanilla hoverText is a computed property override that returns the
    // localized "Buy {hatName}" / "Already own {hatName}" / "Can't afford"
    // string.  The override has no setter, so we cannot assign to it from
    // outside; instead we Postfix the getter and append " [<APItemName>]" to
    // whatever the base computed.
    //
    // The AP item name comes from HatShopRestockLabelPatch.ScoutedAPItemNames,
    // populated on the master client by HatShopAPSyncBehaviour.ScoutAndLabel-
    // Coroutine and on non-master / late-joining clients by the
    // RPCA_SyncArchipelagoLabels handler.
    // =========================================================================

    [HarmonyPatch(typeof(HatBuyInteractable), nameof(HatBuyInteractable.hoverText), MethodType.Getter)]
    internal static class HatBuyInteractableHoverTextPatch
    {
        [HarmonyPostfix]
        private static void Postfix(HatBuyInteractable __instance, ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            if (__instance == null) return;

            if (HatShopRestockLabelPatch.ScoutedAPItemNames.TryGetValue(__instance, out string apItemName)
                && !string.IsNullOrEmpty(apItemName))
            {
                __result = $"{__result} [{apItemName}]";
            }
        }
    }
}
