// Data/ItemNames.cs — mirrors cw-apworld/ap_world/names/item_names.py as C# constants.
// Base item ID: 98765000  (item_base_id in items.py)

namespace ContentWarningArchipelago.Data
{
    public static class ItemNames
    {
        // ---- Progressive Camera ----
        public const string ProgCamera      = "Progressive Camera";   // 3 copies

        // ---- Progressive Oxygen ----
        public const string ProgOxygen      = "Progressive Oxygen";   // 4 copies

        // ---- Diving Bell Upgrades ----
        public const string DivingBellO2       = "Diving Bell O2 Refill";
        public const string DivingBellCharger  = "Diving Bell Charger";

        // ---- Progressive Views ----
        public const string ProgViews       = "Progressive Views";    // 12 copies

        // ---- Progressive Stamina ----
        public const string ProgStamina     = "Progressive Stamina";  // 4 copies

        // ---- Rescue / Safety ----
        public const string RescueHook    = "Rescue Hook";
        public const string ShockStick    = "Shock Stick";
        public const string Defibrillator = "Defibrillator";

        // ---- Money ($) — filler ----
        // Names match item_names.py exactly: $100 / $200 / $300 / $400
        // The values represent the nominal dollar amount shown to the player.
        // In-game the amounts are granted 1-to-1 (i.e. $100 → +$100 in lobby wallet).
        public const string MoneySmall  = "$100";   // offset 20 — ID 98765020
        public const string MoneyMedium = "$200";   // offset 21 — ID 98765021
        public const string MoneyLarge  = "$300";   // offset 22 — ID 98765022
        public const string MoneyXLarge = "$400";   // offset 23 — ID 98765023

        // ---- Meta Coins — filler ----
        // Names match item_names.py: 500 / 1,000 / 1,500 / 2,000 Meta Coins
        public const string MetaCoinsSmall  = "500 Meta Coins";    // offset 30 — ID 98765030
        public const string MetaCoinsMedium = "1,000 Meta Coins";  // offset 31 — ID 98765031
        public const string MetaCoinsLarge  = "1,500 Meta Coins";  // offset 32 — ID 98765032
        public const string MetaCoinsXLarge = "2,000 Meta Coins";  // offset 33 — ID 98765033

        // ---- Traps ----
        public const string MonsterSpawn = "Monster Spawn Trap";
        public const string RagdollTrap  = "Ragdoll Trap";

        // ---- Victory / Event ----
        public const string ViralSensation = "Viral Sensation";
    }
}
