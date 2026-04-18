// Plugin.cs — BepInEx entry point for the Content Warning Archipelago mod.
// Modelled after R.E.P.O.-Archipelago-Client-Mod/Core/Plugin.cs.

using BepInEx;
using BepInEx.Logging;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using HarmonyLib;

namespace ContentWarningArchipelago
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        // ------------------------------------------------------------------ static access points

        /// <summary>Shared logger — accessible from all patches and data classes.</summary>
        internal new static ManualLogSource Logger = null!;

        /// <summary>The single ArchipelagoClient instance for the session.</summary>
        public static ArchipelagoClient connection = null!;

        // ------------------------------------------------------------------ AP connection fields (set by the UI)

        public static string apAddress  = "archipelago.gg";
        public static string apPort     = "38281";
        public static string apPassword = "";
        public static string apSlot     = "";

        // ------------------------------------------------------------------ Unity lifecycle

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"[CWArch] {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} loading…");

            // Initialise data tables.
            ItemData.Init();
            LocationData.Init();

            // Apply all Harmony patches declared in this assembly.
            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

            Logger.LogInfo("[CWArch] Harmony patches applied.");
        }

        private void Start()
        {
            connection = new ArchipelagoClient();
            Logger.LogInfo("[CWArch] ArchipelagoClient created. Waiting for connection.");
        }

        // ------------------------------------------------------------------ Frame tick

        /// <summary>
        /// Advance the three coroutine-based handlers once per frame.
        /// This is the same pattern used by the REPO reference mod.
        /// </summary>
        private void Update()
        {
            if (!connection.connected) return;

            connection.checkItemsReceived?.MoveNext();
            connection.incomingItemHandler?.MoveNext();
            connection.outgoingCheckHandler?.MoveNext();
        }

        // ------------------------------------------------------------------ Public API

        /// <summary>
        /// Called by UI / debug code to initiate the AP connection.
        /// Returns immediately; connection happens asynchronously.
        /// </summary>
        public static void Connect()
        {
            if (!int.TryParse(apPort, out int port))
            {
                Logger.LogError($"[CWArch] Invalid port: '{apPort}'");
                return;
            }
            _ = connection.TryConnect(apAddress, port, apPassword, apSlot);
        }

        /// <summary>Disconnect from the AP server.</summary>
        public static void Disconnect() => connection.TryDisconnect();

        /// <summary>
        /// Shorthand used by patches to send a location check.
        /// No-ops if not connected.
        /// </summary>
        public static void SendCheck(long locationId) => connection.ActivateCheck(locationId);
    }
}
