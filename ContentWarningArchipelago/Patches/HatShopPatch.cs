// Patches/HatShopPatch.cs
//
// Overrides hat shop pricing so every hat's Meta Coin cost is fixed to its
// Archipelago game-stage tier instead of the vanilla random multiplier.
//
// Vanilla behaviour (HatShop.Restock):
//   price = Mathf.RoundToInt(hat.GetBasePrice() * Random.Range(0.5f, 2f) / 10f) * 10
//   where GetBasePrice() maps RARITY → {300, 400, 500, 750, 1500, 3000}.
//
// AP override:
//   Early-stage hats  →   500 MC  (cheap, accessible in the opening days)
//   Mid-stage hats    → 1,000 MC  (requires some meta-coin accumulation)
//   Late-stage hats   → 2,000 MC  (expensive; gated behind late-game income)
//
// These tiers match the hat game_stage values in the AP world's locations.py.
//
// Lookup strategy (first match wins):
//   1. hat.displayName  — raw prefab-set name (most stable across patches)
//   2. hat.GetName()    — localized display string (catches renamed prefabs)
//   3. RARITY fallback  — common/uncommon → 500, rare/epic → 1 000, legendary/mythic → 2 000
//
// The postfix runs after HatShop.Restock() has already called LoadHat() on
// every slot (which sets ihat.priceToday and priceText.text).  We simply
// overwrite those two values; no RPC or Photon state is involved because
// Restock() is already called via RPCA_StockShop on all clients, so every
// client runs this postfix and sets their own local price display.

using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Harmony postfix on <c>HatShop.Restock()</c>.
    /// Replaces the vanilla random price with a fixed AP-stage price after the
    /// shop has been stocked.
    /// </summary>
    [HarmonyPatch(typeof(HatShop), nameof(HatShop.Restock))]
    internal static class HatShopRestockPatch
    {
        // =====================================================================
        // Stage price table
        // Keys match hat.displayName (prefab field) with OrdinalIgnoreCase so
        // minor capitalisation differences don't cause misses.
        // Both the AP location name variant ("News Cap") and the known in-game
        // variant ("Newsboy Cap") are listed for robustness.
        // =====================================================================

        private static readonly Dictionary<string, int> HatStagePrices =
            new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase)
        {
            // ── Early hats — 500 MC ───────────────────────────────────────────
            { "Beanie",          500 },
            { "Bucket Hat",      500 },
            { "Floppy Hat",      500 },
            { "Homburg",         500 },
            { "Bowler Hat",      500 },
            { "Cap",             500 },
            { "News Cap",        500 },
            { "Newsboy Cap",     500 },   // in-game display name variant
            { "Sports Helmet",   500 },
            { "Hard Hat",        500 },

            // ── Mid hats — 1,000 MC ───────────────────────────────────────────
            { "Chefs Hat",      1000 },
            { "Chef's Hat",     1000 },   // apostrophe variant
            { "Propeller Hat",  1000 },
            { "Cowboy Hat",     1000 },
            { "Horns",          1000 },
            { "Hotdog Hat",     1000 },
            { "Hot Dog Hat",    1000 },   // spaced variant
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
            { "Jester's Hat",   2000 },   // apostrophe variant
            { "Ghost Hat",      2000 },
            { "Tooop Hat",      2000 },
            { "Shroom Hat",     2000 },
            { "Witch Hat",      2000 },
            { "Savannah Hair",  2000 },
        };

        // =====================================================================
        // RARITY fallback
        // Covers any hat whose displayName is not in the table above.
        // Maps vanilla RARITY tiers onto the three AP price brackets.
        // =====================================================================

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

        // =====================================================================
        // Postfix — runs after HatShop.Restock() has stocked every slot
        // =====================================================================

        [HarmonyPostfix]
        private static void Postfix(HatShop __instance)
        {
            foreach (HatBuyInteractable slot in __instance.hatBuyInteractables)
            {
                if (slot == null || slot.IsEmpty || slot.ihat == null)
                    continue;

                int price;
                bool fromTable = true;

                // ── 1. Match by displayName (prefab-set, most stable) ─────────
                if (!HatStagePrices.TryGetValue(slot.ihat.displayName, out price))
                {
                    // ── 2. Match by localized name (GetName) ──────────────────
                    string localName = slot.ihat.GetName();
                    if (!HatStagePrices.TryGetValue(localName, out price))
                    {
                        // ── 3. RARITY fallback ────────────────────────────────
                        price     = RarityFallbackPrice(slot.ihat.rarity);
                        fromTable = false;

                        Plugin.Logger.LogDebug(
                            $"[HatShopRestockPatch] No stage mapping for " +
                            $"'{slot.ihat.displayName}' / '{localName}' " +
                            $"(rarity: {slot.ihat.rarity}) — " +
                            $"using fallback price {price} MC.");
                    }
                }

                // Overwrite the price set by LoadHat() in vanilla Restock().
                slot.ihat.priceToday = price;
                slot.priceText.text  = price + " MC";

                Plugin.Logger.LogDebug(
                    $"[HatShopRestockPatch] '{slot.ihat.GetName()}' → " +
                    $"{price} MC " +
                    $"({(fromTable ? "table" : "rarity fallback")})");
            }
        }
    }
}
