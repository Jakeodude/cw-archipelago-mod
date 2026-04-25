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
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // =========================================================================
    // 1. FIXED STOCK — prefix on HatShop.RPCA_StockShop
    // Replaces the incoming date-based seed with the AP session seed hash so
    // the hat selection is stable for the entire AP session.
    // =========================================================================

    [HarmonyPatch(typeof(HatShop), "RPCA_StockShop")]
    internal static class HatShopFixedSeedPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ref int seed)
        {
            if (!Plugin.connection.connected) return;

            string? apSeed = Plugin.connection.session?.RoomState.Seed;
            if (!string.IsNullOrEmpty(apSeed))
            {
                seed = apSeed.GetHashCode();
                Plugin.Logger.LogInfo(
                    $"[HatShopFixedSeedPatch] Hat shop seed overridden to AP seed hash: {seed}");
            }
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
                    slot.nameText.text = labelText;

                HatShopRestockLabelPatch.ScoutedNames[slot] = labelText;
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
                    slot.nameText.text = label;

                // Also update ScoutedNames so LateJoinSyncPatch has accurate data.
                HatShopRestockLabelPatch.ScoutedNames[slot] = label;
            }

            Plugin.Logger.LogInfo(
                $"[HatShopAPSyncBehaviour] RPCA_SyncArchipelagoLabels: applied {labels.Length} label(s).");
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
}
