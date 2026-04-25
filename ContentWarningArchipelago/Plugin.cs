// Plugin.cs — BepInEx entry point for the Content Warning Archipelago mod.
// Modelled after R.E.P.O.-Archipelago-Client-Mod/Core/Plugin.cs.

using BepInEx;
using BepInEx.Logging;
using ContentWarningArchipelago.Core;
using ContentWarningArchipelago.Data;
using ContentWarningArchipelago.UI;
using HarmonyLib;
using MyceliumNetworking;
using Photon.Pun;
using Photon.Realtime;
using Steamworks;

namespace ContentWarningArchipelago
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInDependency("RugbugRedfern.MyceliumNetworking")]
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

        // ------------------------------------------------------------------ Networking constants

        /// <summary>
        /// Unique mod ID for Mycelium networking.
        /// </summary>
        internal const uint MyceliumModId = 2718281828;

        // ------------------------------------------------------------------ static instance

        /// <summary>
        /// Static reference to the plugin MonoBehaviour.
        /// Used by static helpers (e.g. <see cref="Patches.TrapHandler"/>) that need to
        /// start Unity coroutines without holding a MonoBehaviour reference themselves.
        /// </summary>
        public static Plugin Instance { get; private set; } = null!;

        // ------------------------------------------------------------------ Unity lifecycle

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;
            Logger.LogInfo($"[CWArch] {MyPluginInfo.PLUGIN_GUID} v{MyPluginInfo.PLUGIN_VERSION} loading…");

            // Bind persistent config (BetterOxygen pattern).
            APConfig = new APConfig(base.Config);
            Logger.LogInfo($"[CWArch] Config loaded. AP address: {apAddress}:{APConfig.port.Value}");

            // Initialise data tables.
            ItemData.Init();
            LocationData.Init();

            // Apply all Harmony patches declared in this assembly.
            // NOTE: MainMenuAPPatch is picked up by PatchAll() automatically via its
            // [HarmonyPatch(typeof(MainMenuHandler), "Start")] attribute — no manual wiring needed.
            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            harmony.PatchAll();

            // Register with Mycelium networking.
            MyceliumNetwork.RegisterNetworkObject(this, MyceliumModId);

            Logger.LogInfo("[CWArch] Harmony patches applied.");
        }

        private void Start()
        {
            connection = new ArchipelagoClient();
            Logger.LogInfo("[CWArch] ArchipelagoClient created. Waiting for connection.");
            // AP panel injection is handled automatically by the
            // MainMenuAPPatch [HarmonyPatch(typeof(MainMenuHandler), "Start")] postfix —
            // no manual call needed here.
        }

        private void OnDestroy()
        {
            MyceliumNetwork.DeregisterNetworkObject(this, MyceliumModId);
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

        // ------------------------------------------------------------------ Mycelium RPCs

        [CustomRPC]
        internal void LocationFound(string locName, RPCInfo info)
        {
            // Only show notification if not from self (since local player already sees it)
            if (info.SenderSteamID != SteamUser.GetSteamID())
            {
                Logger.LogDebug(
                    $"[CWArch] Received remote location-found notification: '{locName}'");
                APNotificationUI.ShowLocationFound(locName);
            }
        }

        // ------------------------------------------------------------------ Public API

        /// <summary>
        /// True while an Archipelago connection attempt is in progress.
        /// Polled each frame by <see cref="UI.APConnectionPanelUI"/> to drive the
        /// "Connecting…" status label.
        /// </summary>
        public static bool isConnecting { get; private set; } = false;

        /// <summary>
        /// Called by UI / debug code to initiate the AP connection.
        /// Sets <see cref="isConnecting"/> for the duration of the attempt so that
        /// the in-game panel can display a "Connecting…" status.
        /// </summary>
        public static async void Connect()
        {
            if (isConnecting || connection.connected) return;

            isConnecting = true;
            try
            {
                await connection.TryConnect(apAddress, apPort, apPassword, apSlot);
            }
            finally
            {
                isConnecting = false;
            }
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
