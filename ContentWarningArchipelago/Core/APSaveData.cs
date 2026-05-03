// Core/APSaveData.cs
// Persists checked locations and received-item index across game sessions.
// Uses a simple JSON file under Application.persistentDataPath/archipelago/.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace ContentWarningArchipelago.Core
{
    /// <summary>Raw data that gets serialised to / deserialised from disk.</summary>
    [Serializable]
    public class APSaveData
    {
        /// <summary>All location IDs sent to the AP server so far.</summary>
        public List<long> locationsChecked = new();

        /// <summary>
        /// Index into session.Items.AllItemsReceived — everything below this index
        /// has already been processed so we skip re-applying it on reconnect.
        /// </summary>
        public int itemReceivedIndex = 0;

        // ------------------------------------------------------------------ AP slot data cache
        // Populated on each connect from ArchipelagoSession.SlotData.  Persisted
        // here so reconnects after a crash still have the values handy without a
        // re-handshake.  Field names match the apworld fill_slot_data keys
        // (cw-apworld/__init__.py).  Defaults match the apworld defaults so a
        // mid-implementation slot that omits a key still behaves sensibly.

        public int  quotaCount       = 5;
        public bool quotaRequirement = true;

        // Goal toggles — any combination may be active; all enabled goals must
        // be satisfied to win (AND semantics).  At least one is always on.
        public bool viralSensationGoal = true;
        public bool viewsGoal          = false;
        public bool quotaGoal          = false;
        public bool monsterHunterGoal  = false;
        public bool hatCollectorGoal   = false;

        // Goal thresholds.
        public int viewsGoalTarget     = 500_000;
        public int monsterHunterCount  = 12;
        public int hatCollectorCount   = 15;

        // Pool toggles.
        public bool viewsChecks         = true;
        public bool includeSponsorships = true;
        public bool sponsorFiller       = true;
        public bool difficultMonsters   = false;
        public bool monsterTiersEnabled = false;
        public bool fillerMultiSightings = true;
        public bool multiplayerMode     = false;

        // ------------------------------------------------------------------ Progressive item levels
        // These are incremented each time the matching AP item is received and
        // are read every frame by the relevant Harmony patches.

        /// <summary>
        /// How many "Progressive Oxygen" copies have been received (0–4).
        /// Each copy adds 60 s to the base oxygen maximum (~500 s).
        /// </summary>
        public int oxygenUpgradeLevel = 0;

        /// <summary>
        /// How many "Progressive Camera" copies have been received (0–3).
        /// Each copy extends the camera's maximum battery time by 30 s (base 90 s).
        /// Applied by <c>ProgressionStatsPatch.CameraUpgradePatch</c>.
        /// </summary>
        public int cameraUpgradeLevel = 0;

        /// <summary>
        /// How many "Progressive Stamina" copies have been received (0–4).
        /// Sets <c>PlayerController.maxStamina</c> to 100 + level × 25.
        /// Applied by <c>ProgressionStatsPatch.StaminaUpgradePatch</c>.
        /// </summary>
        public int staminaUpgradeLevel = 0;

        /// <summary>
        /// How many "Progressive Stamina Regen" copies have been received (0–2).
        /// Each copy adds 0.5 × <c>Time.deltaTime</c> of extra stamina recovery
        /// per frame, so total regen rate = base × (1 + 0.5 × level) — 150 % at
        /// level 1, 200 % at level 2.  Applied by
        /// <c>ProgressionStatsPatch.StaminaRegenUpgradePatch</c>.
        /// </summary>
        public int staminaRegenUpgradeLevel = 0;

        /// <summary>
        /// How many "Progressive Views" copies have been received (0–12).
        /// Each copy multiplies the score→views conversion by 1.1×.
        /// </summary>
        public int viewsMultiplierLevel = 0;

        // ------------------------------------------------------------------ Diving Bell unlocks

        /// <summary>True once "Diving Bell O2 Refill" has been received.</summary>
        public bool diveBellO2Unlocked = false;

        /// <summary>True once "Diving Bell Charger" has been received.</summary>
        public bool diveBellChargerUnlocked = false;

        // ------------------------------------------------------------------ Safety gear unlocks

        /// <summary>True once "Rescue Hook" has been received.</summary>
        public bool rescueHookUnlocked = false;

        /// <summary>True once "Shock Stick" has been received.</summary>
        public bool shockStickUnlocked = false;

        /// <summary>True once "Defibrillator" has been received.</summary>
        public bool defibrillatorUnlocked = false;

        // ------------------------------------------------------------------ Currency queues
        // Money is lobby-shared (only the master client can call AddMoney).
        // If we receive a money item before RoomStats is ready, or while we are
        // not the master client, we store it here and drain it in MoneyPatch.
        //
        // Meta Coins are AP-authoritative via the DataStorage key
        // CW_MetaCoins_{slot} (issue #10) — there is no MC pending queue.

        /// <summary>
        /// Dollars ($) pending to be added to the shared wallet via
        /// <c>RoomStatsHolder.AddMoney()</c>. Only the master client drains this.
        /// </summary>
        public int pendingMoney = 0;

        // ------------------------------------------------------------------ Views tracking
        // Tracked locally on the master client (the only place AddQuota fires).
        // Persisted across reconnects so a crash mid-quota doesn't reset
        // progress.  Quota cycle resets on quota pass/fail.

        /// <summary>Cumulative views earned across all extractions.  Drives
        /// the "Reached N Total Views" milestone checks.</summary>
        public long lifetimeViews = 0L;

        /// <summary>Views earned strictly within the current 3-day quota
        /// cycle.  Drives the Viral Sensation event (1,000,000 in a quota).
        /// Reset by NewWeek (success) and RPC_QuotaFailed (failure).</summary>
        public long currentQuotaViews = 0L;

        /// <summary>True once "Viral Sensation Achieved" has been fired in the
        /// current quota.  Reset alongside <see cref="currentQuotaViews"/>.</summary>
        public bool viralSensationFiredThisQuota = false;

        // ------------------------------------------------------------------ Sponsorships
        // Number of sponsorships completed across the entire AP slot.  Used as
        // the index for the next "Completed Sponsorship N" check.  Persists
        // across run loss/restart per issue #5 Q3 (apworld answer 3).

        public int sponsorshipsCompleted = 0;

        // ------------------------------------------------------------------ Hat shop (session-only)

        /// <summary>
        /// Hats unlocked during the current Archipelago session by purchasing them
        /// from the hat shop.  This is <b>not</b> persisted to disk (<c>[JsonIgnore]</c>)
        /// so it always starts empty when the AP client connects; hats must be
        /// re-purchased each run.
        /// <para>
        /// When AP is active, <c>MetaProgressionHandler.GetUnlockedHats()</c> is
        /// patched to return this set instead of the native save, making hat
        /// ownership purely session-scoped.
        /// </para>
        /// </summary>
        [JsonIgnore]
        public HashSet<int> sessionUnlockedHats = new HashSet<int>();
    }

    /// <summary>Static façade that owns the single save-data instance.</summary>
    public static class APSave
    {
        public static APSaveData saveData { get; private set; } = new();

        private static string _saveFilePath = string.Empty;

        // ------------------------------------------------------------------
        /// <summary>
        /// Creates or loads the save file for the current AP slot + seed.
        /// Must be called after a successful AP login.
        /// </summary>
        public static void Init(string playerName, string seed)
        {
            string dir = Path.Combine(Application.persistentDataPath, "archipelago", "saves");
            Directory.CreateDirectory(dir);

            _saveFilePath = Path.Combine(dir, $"{Sanitise(playerName)}___{Sanitise(seed)}.json");

            if (File.Exists(_saveFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_saveFilePath);
                    var loaded = JsonConvert.DeserializeObject<APSaveData>(json);
                    if (loaded != null) saveData = loaded;
                    Plugin.Logger.LogInfo($"[APSave] Loaded save from {_saveFilePath}");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogError($"[APSave] Failed to load save: {e.Message}. Starting fresh.");
                    saveData = new APSaveData();
                }
            }
            else
            {
                saveData = new APSaveData();
                Plugin.Logger.LogInfo($"[APSave] Created new save at {_saveFilePath}");
            }

            Flush();
        }

        // ------------------------------------------------------------------
        /// <summary>Persist current saveData to disk immediately.</summary>
        public static void Flush()
        {
            if (string.IsNullOrEmpty(_saveFilePath)) return;
            try
            {
                File.WriteAllText(_saveFilePath, JsonConvert.SerializeObject(saveData, Formatting.Indented));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"[APSave] Failed to write save: {e.Message}");
            }
        }

        // ------------------------------------------------------------------
        public static void AddLocationChecked(long locationId)
        {
            if (!saveData.locationsChecked.Contains(locationId))
            {
                saveData.locationsChecked.Add(locationId);
                Flush();
            }
        }

        public static bool IsLocationChecked(long locationId)
            => saveData.locationsChecked.Contains(locationId);

        public static void IncrementItemIndex()
        {
            saveData.itemReceivedIndex++;
            Flush();
        }

        // ------------------------------------------------------------------
        private static string Sanitise(string s)
            => string.Join("_", s.Split(Path.GetInvalidFileNameChars()));
    }
}
