// Data/ItemData.cs
// Maps Archipelago item IDs ↔ item names, matching items.py in the apworld.
// Base ID: 98765000 (item_base_id)   Offset = item_id_offset from the table.

using System.Collections.Generic;
using UnityEngine;

namespace ContentWarningArchipelago.Data
{
    public static class ItemData
    {
        public const long BaseId = 98765000L;

        // Offsets match item_id_offset in items.py exactly.
        public const int OffsetProgCamera       = 0;
        public const int OffsetProgOxygen       = 1;
        public const int OffsetDivingBellO2     = 2;
        public const int OffsetDivingBellCharger= 3;
        public const int OffsetProgViews        = 4;
        public const int OffsetRescueHook       = 10;
        public const int OffsetShockStick       = 11;
        public const int OffsetDefibrillator    = 12;
        public const int OffsetMoneySmall       = 20;
        public const int OffsetMoneyMedium      = 21;
        public const int OffsetMoneyLarge       = 22;
        public const int OffsetMetaCoinsSmall   = 30;
        public const int OffsetMetaCoinsMedium  = 31;
        public const int OffsetMetaCoinsLarge   = 32;
        public const int OffsetMonsterSpawn     = 40;
        public const int OffsetRagdollTrap      = 41;

        // Bidirectional lookup tables (populated once in Init).
        public static Dictionary<long, string> IdToName { get; private set; } = new();
        public static Dictionary<string, long> NameToId { get; private set; } = new();

        public static void Init()
        {
            Register(OffsetProgCamera,        ItemNames.ProgCamera);
            Register(OffsetProgOxygen,        ItemNames.ProgOxygen);
            Register(OffsetDivingBellO2,      ItemNames.DivingBellO2);
            Register(OffsetDivingBellCharger, ItemNames.DivingBellCharger);
            Register(OffsetProgViews,         ItemNames.ProgViews);
            Register(OffsetRescueHook,        ItemNames.RescueHook);
            Register(OffsetShockStick,        ItemNames.ShockStick);
            Register(OffsetDefibrillator,     ItemNames.Defibrillator);
            Register(OffsetMoneySmall,        ItemNames.MoneySmall);
            Register(OffsetMoneyMedium,       ItemNames.MoneyMedium);
            Register(OffsetMoneyLarge,        ItemNames.MoneyLarge);
            Register(OffsetMetaCoinsSmall,    ItemNames.MetaCoinsSmall);
            Register(OffsetMetaCoinsMedium,   ItemNames.MetaCoinsMedium);
            Register(OffsetMetaCoinsLarge,    ItemNames.MetaCoinsLarge);
            Register(OffsetMonsterSpawn,      ItemNames.MonsterSpawn);
            Register(OffsetRagdollTrap,       ItemNames.RagdollTrap);
        }

        private static void Register(int offset, string name)
        {
            long id = BaseId + offset;
            IdToName[id] = name;
            NameToId[name] = id;
        }

        public static long GetId(string name)
            => NameToId.TryGetValue(name, out var id) ? id : -1L;

        public static string GetName(long id)
            => IdToName.TryGetValue(id, out var name) ? name : $"Unknown Item ({id})";

        /// <summary>
        /// Apply the effect of a received Archipelago item in-game.
        /// Called from ArchipelagoClient.IncomingItemHandler each time an item is
        /// dequeued for the local player.
        /// </summary>
        public static void HandleReceivedItem(long itemId)
        {
            string name = GetName(itemId);
            Plugin.Logger.LogInfo($"[ItemData] Handling received item: {name} (id={itemId})");

            switch (name)
            {
                // ---- Progressive Camera ----
                // Each copy grants +30 s of film (90→120→150→180).
                // TODO: hook into CameraUpgrades.AddCameraUpgrade() or equivalent.
                case ItemNames.ProgCamera:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant Progressive Camera upgrade.");
                    break;

                // ---- Progressive Oxygen ----
                // Each copy extends oxygen tank capacity.
                // TODO: hook into the oxygen upgrade system.
                case ItemNames.ProgOxygen:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant Progressive Oxygen upgrade.");
                    break;

                // ---- Diving Bell upgrades ----
                case ItemNames.DivingBellO2:
                    Plugin.Logger.LogInfo("[ItemData] TODO: enable Diving Bell O2 Refill.");
                    break;
                case ItemNames.DivingBellCharger:
                    Plugin.Logger.LogInfo("[ItemData] TODO: enable Diving Bell Charger.");
                    break;

                // ---- Progressive Views multiplier ----
                // Each copy multiplies video view income by 1.1×.
                case ItemNames.ProgViews:
                    Plugin.Logger.LogInfo("[ItemData] TODO: apply Progressive Views multiplier.");
                    break;

                // ---- Safety gear ----
                case ItemNames.RescueHook:
                    Plugin.Logger.LogInfo("[ItemData] TODO: unlock Rescue Hook in shop.");
                    break;
                case ItemNames.ShockStick:
                    Plugin.Logger.LogInfo("[ItemData] TODO: unlock Shock Stick in shop.");
                    break;
                case ItemNames.Defibrillator:
                    Plugin.Logger.LogInfo("[ItemData] TODO: unlock Defibrillator in shop.");
                    break;

                // ---- Money filler ----
                case ItemNames.MoneySmall:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant $200.");
                    break;
                case ItemNames.MoneyMedium:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant $400.");
                    break;
                case ItemNames.MoneyLarge:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant $600.");
                    break;

                // ---- Meta Coins filler ----
                case ItemNames.MetaCoinsSmall:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant 1,000 Meta Coins.");
                    break;
                case ItemNames.MetaCoinsMedium:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant 2,000 Meta Coins.");
                    break;
                case ItemNames.MetaCoinsLarge:
                    Plugin.Logger.LogInfo("[ItemData] TODO: grant 3,000 Meta Coins.");
                    break;

                // ---- Traps ----
                case ItemNames.MonsterSpawn:
                    Plugin.Logger.LogInfo("[ItemData] TODO: spawn a random monster (trap).");
                    break;
                case ItemNames.RagdollTrap:
                    Plugin.Logger.LogInfo("[ItemData] TODO: ragdoll the local player for 5 s (trap).");
                    break;

                default:
                    Plugin.Logger.LogWarning($"[ItemData] No handler for item: {name}");
                    break;
            }
        }
    }
}
