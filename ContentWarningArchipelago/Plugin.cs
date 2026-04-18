// Plugin.cs — BepInEx entry point for the Content Warning Archipelago mod.
// Modelled after R.E.P.O.-Archipelago-Client-Mod/Core/Plugin.cs.

using BepInEx;
using BepInEx.Logging;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using HarmonyLib;
using ContentWarningArchipelago.UI;

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

        /// <summary>
        /// BepInEx config binding for AP connection credentials.
        /// Modelled after BetterOxygen's Config.cs — persists between sessions so
        /// the player only needs to enter details once.
        /// </summary>
        public static APConfig APConfig { get; private set; } = null!;

        // ------------------------------------------------------------------ AP connection helpers (read from config)

        public static string apAddress  => APConfig?.address.Value  ?? "archipelago.gg";
        public static int    apPort     => APConfig?.port.Value     ?? 38281;
        public static string apPassword => APConfig?.password.Value ?? "";
        public static string apSlot     => APConfig?.slot.Value     ?? "";

        // ------------------------------------------------------------------ Unity lifecycle

        private void Awake()
        {
            Logger = base.Logger;
            Logger.LogInfo($"[CWArch] {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} loading…");

            // Bind persistent config (BetterOxygen pattern).
            APConfig = new APConfig(base.Config);
            Logger.LogInfo($"[CWArch] Config loaded. AP address: {apAddress}:{APConfig.port.Value}");

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
            _ = connection.TryConnect(apAddress, apPort, apPassword, apSlot);
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
