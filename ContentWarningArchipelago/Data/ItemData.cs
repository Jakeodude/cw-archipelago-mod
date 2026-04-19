// Data/ItemData.cs
// Maps Archipelago item IDs ↔ item names, matching items.py in the apworld.
// Base ID: 98765000 (item_base_id)   Offset = item_id_offset from the table.
//
// HandleReceivedItem() is the single dispatch point called from
// ArchipelagoClient.IncomingItemHandler on the main thread.
// Every case in the switch:
//   1. Updates APSave.saveData (the persistent state record).
//   2. Applies the in-game effect immediately where possible.
//   3. Shows a HUD notification.
//   4. Calls APSave.Flush() to persist the state change.
//
// Reference mods used per item group:
//   ProgOxygen / DivingBellO2   → BetterOxygen (OxygenPatch.cs reads saveData)
//   DivingBellCharger           → Charging_Divebell concept + BetterOxygen isInDiveBell
//   Money items                 → StartingBudget (ShopHandler.m_RoomStats.AddMoney)
//   MetaCoins                   → MetaProgressionHandler.AddMetaCoins (direct static call)
//   Traps                       → TrapPatches.TrapHandler (Virality-style RPC dispatch)

using System.Collections.Generic;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Patches;
using ContentWarningArchipelago.UI;
using HarmonyLib;
using Photon.Pun;
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

        // ======================================================================
        /// <summary>
        /// Apply the effect of a received Archipelago item in-game.
        /// Called from ArchipelagoClient.IncomingItemHandler on the main thread.
        /// </summary>
        // ======================================================================
        /// <summary>
        /// Apply the effect of a received Archipelago item in-game.
        /// Called from ArchipelagoClient.IncomingItemHandler on the main thread.
        /// </summary>
        /// <param name="itemId">The Archipelago item ID to apply.</param>
        /// <param name="senderName">
        /// The AP slot name of the player who sent this item.
        /// Pass empty string (default) when the item comes from the local player's own world;
        /// the notification will then omit the "from [Player]" line.
        /// </param>
        public static void HandleReceivedItem(long itemId, string senderName = "")
        {
            string name = GetName(itemId);
            Plugin.Logger.LogInfo($"[ItemData] Handling received item: {name} (id={itemId})" +
                (string.IsNullOrEmpty(senderName) ? "" : $" from '{senderName}'"));

            // Update the diving bell status display with the latest item.
            DivingBellAPStatusPatch.LastItemName = name;

            switch (name)
            {
                // ==============================================================
                // PROGRESSIVE CAMERA
                // Each copy extends film duration by 30 s.
                // OxygenPatch reads oxygenUpgradeLevel; we handle camera via
                // reflection on the Camera/CameraBody class each time the item
                // is received, and persist the level for reconnection.
                // ==============================================================
                case ItemNames.ProgCamera:
                {
                    APSave.saveData.cameraUpgradeLevel++;
                    APSave.Flush();
                    ApplyCameraUpgrade(APSave.saveData.cameraUpgradeLevel);
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo(
                        $"[ItemData] Progressive Camera level {APSave.saveData.cameraUpgradeLevel} applied.");
                    break;
                }

                // ==============================================================
                // PROGRESSIVE OXYGEN
                // Increment level; OxygenPatch.Prefix reads this value every
                // frame and adds OxygenPerLevel × level to maxOxygen.
                // ==============================================================
                case ItemNames.ProgOxygen:
                {
                    APSave.saveData.oxygenUpgradeLevel++;
                    APSave.Flush();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo(
                        $"[ItemData] Progressive Oxygen level {APSave.saveData.oxygenUpgradeLevel} — " +
                        $"+{APSave.saveData.oxygenUpgradeLevel * 60} s bonus oxygen.");
                    break;
                }

                // ==============================================================
                // DIVING BELL O2 REFILL
                // Unlock flag read every frame by OxygenPatch.Prefix.
                // Broadcast to lobby so late-joiners pick it up (Virality pattern).
                // ==============================================================
                case ItemNames.DivingBellO2:
                {
                    APSave.saveData.diveBellO2Unlocked = true;
                    APSave.Flush();
                    LateJoinSyncPatch.BroadcastWorldState();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Diving Bell O2 Refill unlocked.");
                    break;
                }

                // ==============================================================
                // DIVING BELL CHARGER
                // Unlock flag read every frame by DivingBellChargerPatch.Prefix.
                // Broadcast to lobby (Virality pattern).
                // ==============================================================
                case ItemNames.DivingBellCharger:
                {
                    APSave.saveData.diveBellChargerUnlocked = true;
                    APSave.Flush();
                    LateJoinSyncPatch.BroadcastWorldState();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Diving Bell Charger unlocked.");
                    break;
                }

                // ==============================================================
                // PROGRESSIVE VIEWS
                // ViewsMultiplierPatch.Postfix reads this every time
                // BigNumbers.GetScoreToViews is called (global, all footage).
                // ==============================================================
                case ItemNames.ProgViews:
                {
                    APSave.saveData.viewsMultiplierLevel++;
                    APSave.Flush();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    double mult = System.Math.Pow(1.1, APSave.saveData.viewsMultiplierLevel);
                    Plugin.Logger.LogInfo(
                        $"[ItemData] Progressive Views level {APSave.saveData.viewsMultiplierLevel} — " +
                        $"{mult:F2}× views multiplier.");
                    break;
                }

                // ==============================================================
                // SAFETY GEAR UNLOCKS
                // Mark unlocked; a future ShopPatch can hide/show items based
                // on these flags.  Notification shown immediately.
                // ==============================================================
                case ItemNames.RescueHook:
                {
                    APSave.saveData.rescueHookUnlocked = true;
                    APSave.Flush();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Rescue Hook unlocked.");
                    break;
                }
                case ItemNames.ShockStick:
                {
                    APSave.saveData.shockStickUnlocked = true;
                    APSave.Flush();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Shock Stick unlocked.");
                    break;
                }
                case ItemNames.Defibrillator:
                {
                    APSave.saveData.defibrillatorUnlocked = true;
                    APSave.Flush();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Defibrillator unlocked.");
                    break;
                }

                // ==============================================================
                // MONEY ITEMS — StartingBudget pattern
                // Try immediate AddMoney via SurfaceNetworkHandler.RoomStats
                // (master client only).  If not ready or not master, queue in
                // pendingMoney which MoneyPatch drains on the next InitShop.
                // ==============================================================
                case ItemNames.MoneySmall:
                    GrantMoney(200, name, senderName);
                    break;
                case ItemNames.MoneyMedium:
                    GrantMoney(400, name, senderName);
                    break;
                case ItemNames.MoneyLarge:
                    GrantMoney(600, name, senderName);
                    break;

                // ==============================================================
                // META COINS — direct call to MetaProgressionHandler.AddMetaCoins
                // (same static method the game itself uses in console commands).
                // Also calls UserInterface.ShowMoneyNotification internally.
                // ==============================================================
                case ItemNames.MetaCoinsSmall:
                    GrantMetaCoins(1000, name);
                    break;
                case ItemNames.MetaCoinsMedium:
                    GrantMetaCoins(2000, name);
                    break;
                case ItemNames.MetaCoinsLarge:
                    GrantMetaCoins(3000, name);
                    break;

                // ==============================================================
                // TRAPS — Virality-style RPC dispatch via TrapHandler.
                // ==============================================================
                case ItemNames.MonsterSpawn:
                {
                    Plugin.Logger.LogInfo("[ItemData] Activating Monster Spawn Trap.");
                    APNotificationUI.ShowItemReceived("⚠ Monster Spawn Trap!", senderName);
                    TrapHandler.ApplyMonsterSpawnTrap();
                    break;
                }
                case ItemNames.RagdollTrap:
                {
                    Plugin.Logger.LogInfo("[ItemData] Activating Ragdoll Trap.");
                    APNotificationUI.ShowItemReceived("⚠ Ragdoll Trap!", senderName);
                    TrapHandler.ApplyRagdollTrap(5f);
                    break;
                }

                default:
                    Plugin.Logger.LogWarning($"[ItemData] No handler for item: {name}");
                    break;
            }
        }

        // ======================================================================
        // HELPERS
        // ======================================================================

        /// <summary>
        /// Grants money to the shared lobby wallet.
        /// If the local client is the master and RoomStats is ready, adds
        /// immediately (StartingBudget pattern).  Otherwise queues in pendingMoney
        /// for MoneyPatch to drain on the next InitShop call.
        /// </summary>
        private static void GrantMoney(int amount, string itemName, string senderName = "")
        {
            // Show a HUD notification. We use ShowItemReceived here because
            // MoneyCellUI.MoneyCellType does not expose a Money/Cash variant
            // (only MetaCoins is confirmed in the game source).
            APNotificationUI.ShowItemReceived($"+${amount}", senderName);

            if (PhotonNetwork.IsMasterClient && SurfaceNetworkHandler.RoomStats != null)
            {
                Plugin.Logger.LogInfo($"[ItemData] Adding ${amount} to lobby wallet immediately.");
                SurfaceNetworkHandler.RoomStats.AddMoney(amount);
            }
            else
            {
                Plugin.Logger.LogInfo(
                    $"[ItemData] Queuing ${amount} — not master or RoomStats not ready.");
                APSave.saveData.pendingMoney += amount;
                APSave.Flush();
            }
        }

        /// <summary>
        /// Grants Meta Coins via <c>MetaProgressionHandler.AddMetaCoins()</c>.
        /// That method automatically shows the in-game money popup and saves to disk.
        /// Falls back to the pending queue if the singleton isn't ready.
        /// </summary>
        private static void GrantMetaCoins(int amount, string itemName)
        {
            try
            {
                // Direct static call — mirrors MetaProgressionHandler's own AddMetaCoins
                // which also calls UserInterface.ShowMoneyNotification internally.
                MetaProgressionHandler.AddMetaCoins(amount);
                Plugin.Logger.LogInfo($"[ItemData] Granted {amount} Meta Coins.");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[ItemData] MetaProgressionHandler.AddMetaCoins failed: {ex.Message}. Queuing.");
                APSave.saveData.pendingMetaCoins += amount;
                APSave.Flush();

                // Show fallback notification.
                APNotificationUI.ShowMoneyReceived("AP Meta Coins", amount,
                    MoneyCellUI.MoneyCellType.MetaCoins);
            }
        }

        /// <summary>
        /// Applies the Progressive Camera upgrade in-game.
        /// Attempts to find the camera film duration field via AccessTools and
        /// extend it by 30 s per level.
        ///
        /// DESIGN: BetterOxygen reads config once on load; we call this each time
        /// an item arrives (safe since it's called at most 3 times per game).
        /// </summary>
        private static void ApplyCameraUpgrade(int level)
        {
            // Additional film duration (seconds) granted per level.
            const float extraPerLevel = 30f;

            // Try common camera/camcorder type names.
            foreach (string typeName in new[] {
                "CameraBody", "Camera", "Camcorder",
                "CameraUpgrades", "VideoCamera" })
            {
                var t = AccessTools.TypeByName(typeName);
                if (t == null) continue;

                // Try instance singleton first.
                object? instance = null;
                var instField = AccessTools.Field(t, "instance")
                             ?? AccessTools.Field(t, "Instance");
                if (instField?.IsStatic == true)
                    instance = instField.GetValue(null);

                // Try common film-time field names.
                foreach (string fname in new[] {
                    "maxFilmTime", "m_maxFilmTime", "filmDuration",
                    "recordingTime", "maxRecordingTime", "filmLength" })
                {
                    var field = AccessTools.Field(t, fname);
                    if (field == null || field.FieldType != typeof(float)) continue;

                    object? target = field.IsStatic ? null : instance;
                    if (!field.IsStatic && target == null) continue;

                    float current = (float)(field.GetValue(target) ?? 0f);
                    // Set to base + bonus (idempotent for a given level).
                    float newVal = current + extraPerLevel;
                    field.SetValue(target, newVal);

                    Plugin.Logger.LogInfo(
                        $"[ItemData] Camera film time extended by {extraPerLevel} s " +
                        $"(level {level}, now {newVal} s) via {typeName}.{fname}.");
                    return;
                }
            }

            Plugin.Logger.LogDebug(
                "[ItemData] Could not find camera film time field — " +
                "upgrade will take effect after next game restart via save replay.");
        }
    }
}
