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

        /// <summary>AP slot data values cached on connect (read from server).</summary>
        public int  quotaCount        = 5;      // how many quotas the world uses
        public bool viralSensationGoal = true;  // win condition is "Viral Sensation"

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

        /// <summary>
        /// Dollars ($) pending to be added to the shared wallet via
        /// <c>RoomStatsHolder.AddMoney()</c>. Only the master client drains this.
        /// </summary>
        public int pendingMoney = 0;

        /// <summary>
        /// Meta Coins pending grant. Normally applied immediately via
        /// <c>MetaProgressionHandler.AddMetaCoins()</c>, but queued here if the
        /// singleton isn't ready yet.
        /// </summary>
        public int pendingMetaCoins = 0;

        // ------------------------------------------------------------------ Monster / artifact tiers

        /// <summary>
        /// True when the AP world was generated with <c>monster_tiers</c> enabled.
        /// Cached from slot data on connect; used by <c>ContentEvaluatorPatch</c> to
        /// decide whether to attempt tier 2/3 filming checks.
        /// </summary>
        public bool monsterTiersEnabled = false;

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
