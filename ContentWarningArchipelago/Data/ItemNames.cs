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

        // ---- Rescue / Safety ----
        public const string RescueHook    = "Rescue Hook";
        public const string ShockStick    = "Shock Stick";
        public const string Defibrillator = "Defibrillator";

        // ---- Money ($) — filler ----
        public const string MoneySmall  = "$200";
        public const string MoneyMedium = "$400";
        public const string MoneyLarge  = "$600";

        // ---- Meta Coins — filler ----
        public const string MetaCoinsSmall  = "1,000 Meta Coins";
        public const string MetaCoinsMedium = "2,000 Meta Coins";
        public const string MetaCoinsLarge  = "3,000 Meta Coins";

        // ---- Traps ----
        public const string MonsterSpawn = "Monster Spawn Trap";
        public const string RagdollTrap  = "Ragdoll Trap";

        // ---- Victory / Event ----
        public const string ViralSensation = "Viral Sensation";
    }
}
