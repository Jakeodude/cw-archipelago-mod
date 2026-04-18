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
