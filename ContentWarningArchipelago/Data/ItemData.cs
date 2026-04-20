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
//
// Progressive stat items (Camera, Stamina) are applied immediately via
// ProgressionStatsPatch static helpers so the buff takes effect mid-game
// without requiring a day restart.

using System;
using System.Collections.Generic;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Patches;
using ContentWarningArchipelago.UI;
using Photon.Pun;
using UnityEngine;
using Zorro.Core;

namespace ContentWarningArchipelago.Data
{
    public static class ItemData
    {
        public const long BaseId = 98765000L;

        // Offsets match item_id_offset in items.py exactly.
        public const int OffsetProgCamera        = 0;
        public const int OffsetProgOxygen        = 1;
        public const int OffsetDivingBellO2      = 2;
        public const int OffsetDivingBellCharger = 3;
        public const int OffsetProgViews         = 4;
        public const int OffsetProgStamina       = 5;
        public const int OffsetRescueHook        = 10;
        public const int OffsetShockStick        = 11;
        public const int OffsetDefibrillator     = 12;
        public const int OffsetMoneySmall        = 20;
        public const int OffsetMoneyMedium       = 21;
        public const int OffsetMoneyLarge        = 22;
        public const int OffsetMetaCoinsSmall    = 30;
        public const int OffsetMetaCoinsMedium   = 31;
        public const int OffsetMetaCoinsLarge    = 32;
        public const int OffsetMonsterSpawn      = 40;
        public const int OffsetRagdollTrap       = 41;

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
            Register(OffsetProgStamina,       ItemNames.ProgStamina);
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
        /// <param name="itemId">The Archipelago item ID to apply.</param>
        /// <param name="senderName">
        /// The AP slot name of the player who sent this item.
        /// Pass empty string (default) when the item comes from the local player's own world;
        /// the notification will then omit the "from [Player]" line.
        /// </param>
        // ======================================================================
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
                // Each copy extends camera battery by 30 s (base 90 s).
                // ProgressionStatsPatch.CameraUpgradePatch applies on
                // VideoCamera.ConfigItem; we also call ApplyCameraUpgrade here
                // to sync any cameras already in-world mid-day.
                // ==============================================================
                case ItemNames.ProgCamera:
                {
                    APSave.saveData.cameraUpgradeLevel++;
                    APSave.Flush();
                    ProgressionStatsPatch.ApplyCameraUpgrade(APSave.saveData.cameraUpgradeLevel);
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo(
                        $"[ItemData] Progressive Camera level {APSave.saveData.cameraUpgradeLevel} — " +
                        $"battery {90 + APSave.saveData.cameraUpgradeLevel * 30} s.");
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
                // ContentEvaluatorPatch multiplies ContentBuffer item scores by
                // (1.0 + level × 0.1) before GenerateComments converts them to
                // view counts.  Does NOT affect the quota difficulty display.
                // ==============================================================
                case ItemNames.ProgViews:
                {
                    APSave.saveData.viewsMultiplierLevel++;
                    APSave.Flush();
                    APNotificationUI.ShowItemReceived(name, senderName);
                    float mult = 1.0f + APSave.saveData.viewsMultiplierLevel * 0.1f;
                    Plugin.Logger.LogInfo(
                        $"[ItemData] Progressive Views level {APSave.saveData.viewsMultiplierLevel} — " +
                        $"{mult:F1}× footage view multiplier.");
                    break;
                }

                // ==============================================================
                // PROGRESSIVE STAMINA
                // ProgressionStatsPatch.StaminaUpgradePatch applies on
                // Player.Awake; ApplyStaminaUpgrade syncs the local player
                // immediately if they are already alive mid-day.
                // ==============================================================
                case ItemNames.ProgStamina:
                {
                    APSave.saveData.staminaUpgradeLevel++;
                    APSave.Flush();
                    ProgressionStatsPatch.ApplyStaminaUpgrade(APSave.saveData.staminaUpgradeLevel);
                    APNotificationUI.ShowItemReceived(name, senderName);
                    Plugin.Logger.LogInfo(
                        $"[ItemData] Progressive Stamina level {APSave.saveData.staminaUpgradeLevel} — " +
                        $"maxStamina {100 + APSave.saveData.staminaUpgradeLevel * 25}.");
                    break;
                }

                // ==============================================================
                // SAFETY GEAR UNLOCKS — lobby items
                // 1. Persist the unlock flag (read by ShopPatch / gate logic).
                // 2. Physically spawn the item in the lobby using
                //    PickupHandler.CreatePickup, which calls
                //    PhotonNetwork.InstantiateRoomObject("PickupHolder", ...).
                //    That creates a room-owned object (master-client authority)
                //    with a proper PhotonView, preventing desyncs.
                //    Only the master client may call CreatePickup; non-master
                //    clients rely on Photon's object-sync to see the pickup.
                // ==============================================================
                case ItemNames.RescueHook:
                {
                    APSave.saveData.rescueHookUnlocked = true;
                    APSave.Flush();
                    SpawnLobbyItem("Rescue Hook", name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Rescue Hook unlocked.");
                    break;
                }
                case ItemNames.ShockStick:
                {
                    APSave.saveData.shockStickUnlocked = true;
                    APSave.Flush();
                    SpawnLobbyItem("Shock Stick", name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Shock Stick unlocked.");
                    break;
                }
                case ItemNames.Defibrillator:
                {
                    APSave.saveData.defibrillatorUnlocked = true;
                    APSave.Flush();
                    SpawnLobbyItem("Defibrillator", name, senderName);
                    Plugin.Logger.LogInfo("[ItemData] Defibrillator unlocked.");
                    break;
                }

                // ==============================================================
                // MONEY ITEMS — StartingBudget pattern
                // Try immediate AddMoney via SurfaceNetworkHandler.RoomStats
                // (master client only).  If not ready or not master, queue in
                // pendingMoney which MoneyPatch drains on the next InitShop.
                //
                // HOST PRIORITY: Only the master client applies money grants.
                // Non-master clients skip these entirely to prevent doubling the
                // shared wallet when all players receive the same AP item.
                // ==============================================================
                case ItemNames.MoneySmall:
                    if (PhotonNetwork.IsMasterClient) GrantMoney(200, name, senderName);
                    break;
                case ItemNames.MoneyMedium:
                    if (PhotonNetwork.IsMasterClient) GrantMoney(400, name, senderName);
                    break;
                case ItemNames.MoneyLarge:
                    if (PhotonNetwork.IsMasterClient) GrantMoney(600, name, senderName);
                    break;

                // ==============================================================
                // META COINS — direct call to MetaProgressionHandler.AddMetaCoins
                // (same static method the game itself uses in console commands).
                // Also calls UserInterface.ShowMoneyNotification internally.
                //
                // HOST PRIORITY: Only the master client grants MetaCoins.
                // MetaProgressionHandler.AddMetaCoins writes to a shared save; if
                // every connected client called it, each player would receive
                // the full amount independently, effectively multiplying the grant
                // by the player count.
                // ==============================================================
                case ItemNames.MetaCoinsSmall:
                    if (PhotonNetwork.IsMasterClient) GrantMetaCoins(1000, name);
                    break;
                case ItemNames.MetaCoinsMedium:
                    if (PhotonNetwork.IsMasterClient) GrantMetaCoins(2000, name);
                    break;
                case ItemNames.MetaCoinsLarge:
                    if (PhotonNetwork.IsMasterClient) GrantMetaCoins(3000, name);
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
        /// Physically spawns a lobby (surface-scene) item as a networked pickup.
        /// <para>
        /// Uses <c>PickupHandler.CreatePickup</c> which internally calls
        /// <c>PhotonNetwork.InstantiateRoomObject("PickupHolder", ...)</c>.
        /// Room objects are owned by the master client and carry a <c>PhotonView</c>,
        /// so all connected clients see the same object without desync.
        /// </para>
        /// <para>
        /// Only the master client actually instantiates the object; non-master
        /// clients receive the pickup via Photon's normal object-sync.
        /// The HUD notification is shown on every client regardless.
        /// </para>
        /// </summary>
        /// <param name="gameDisplayName">
        /// The <c>Item.displayName</c> string exactly as it appears in the game's
        /// <c>ItemDatabase</c> (e.g. "Rescue Hook", "Shock Stick", "Defibrillator").
        /// </param>
        /// <param name="apItemName">The AP item name shown in the HUD notification.</param>
        /// <param name="senderName">The AP slot name of the sending player (empty = self).</param>
        private static void SpawnLobbyItem(string gameDisplayName, string apItemName, string senderName)
        {
            // Always show the HUD notification on every client.
            APNotificationUI.ShowItemReceived(apItemName, senderName);

            // Only the master client may call PickupHandler.CreatePickup.
            // (CreatePickup already guards this internally, but we skip the
            // lookup cost entirely for non-master clients.)
            if (!PhotonNetwork.IsMasterClient)
            {
                Plugin.Logger.LogInfo(
                    $"[ItemData] SpawnLobbyItem '{gameDisplayName}': not master client — " +
                    "pickup will be created by host and synced via Photon.");
                return;
            }

            // Locate the item in the game's runtime ItemDatabase by display name.
            Item foundItem = null;
            foreach (Item item in SingletonAsset<ItemDatabase>.Instance.Objects)
            {
                if (string.Equals(item.displayName, gameDisplayName, System.StringComparison.OrdinalIgnoreCase))
                {
                    foundItem = item;
                    break;
                }
            }

            if (foundItem == null)
            {
                Plugin.Logger.LogWarning(
                    $"[ItemData] SpawnLobbyItem: item '{gameDisplayName}' not found in ItemDatabase — " +
                    "cannot spawn pickup. Check that the display name matches exactly.");
                return;
            }

            // Spawn near the local player (who is the master client in the lobby).
            // If for some reason localPlayer is null, fall back to world origin + up.
            Vector3 spawnPos = (object)Player.localPlayer != null
                ? Player.localPlayer.transform.position + Vector3.up
                : Vector3.up * 2f;

            // PickupHandler.CreatePickup calls:
            //   PhotonNetwork.InstantiateRoomObject("PickupHolder", pos, rot, 0)
            // which creates a room-owned GameObject with a PhotonView, then fires
            // RPC_ConfigurePickup(AllBuffered) so every client initialises the item.
            Pickup pickup = PickupHandler.CreatePickup(
                foundItem.id,
                new ItemInstanceData(Guid.NewGuid()),
                spawnPos,
                Quaternion.identity);

            if (pickup != null)
                Plugin.Logger.LogInfo(
                    $"[ItemData] SpawnLobbyItem: spawned '{gameDisplayName}' (id={foundItem.id}) " +
                    $"at {spawnPos} with PhotonView {pickup.m_photonView?.ViewID}.");
            else
                Plugin.Logger.LogWarning(
                    $"[ItemData] SpawnLobbyItem: PickupHandler.CreatePickup returned null " +
                    $"for '{gameDisplayName}'.");
        }
    }
}
