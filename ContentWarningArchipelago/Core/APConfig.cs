// Core/APConfig.cs
// BepInEx ConfigFile-backed AP connection settings.
// Modelled after BetterOxygen/Config.cs — binds persistent entries so the
// player only needs to enter their connection details once.

using BepInEx.Configuration;

namespace ContentWarningArchipelago.Core
{
    /// <summary>
    /// Wraps the BepInEx <see cref="ConfigFile"/> and exposes typed
    /// <see cref="ConfigEntry{T}"/> fields for every AP connection credential.
    /// Instantiated once in <see cref="Plugin.Awake"/> and stored as
    /// <see cref="Plugin.Config"/>.
    /// </summary>
    public class APConfig
    {
        // ------------------------------------------------------------------ entries

        /// <summary>Archipelago server hostname or IP (e.g. "archipelago.gg").</summary>
        public readonly ConfigEntry<string> address;

        /// <summary>Archipelago server port (default 38281).</summary>
        public readonly ConfigEntry<int> port;

        /// <summary>Slot / player name as registered in the AP multiworld.</summary>
        public readonly ConfigEntry<string> slot;

        /// <summary>Room password — leave blank if the room has no password.</summary>
        public readonly ConfigEntry<string> password;

        // ------------------------------------------------------------------ constructor

        public APConfig(ConfigFile cfg)
        {
            // Save-on-set is left on (default) so every edit is persisted immediately.

            address = cfg.Bind(
                "Connection",
                "Address",
                "archipelago.gg",
                "Hostname or IP of the Archipelago server.");

            port = cfg.Bind(
                "Connection",
                "Port",
                38281,
                "Port of the Archipelago server (default 38281).");

            slot = cfg.Bind(
                "Connection",
                "Slot",
                "",
                "Your slot / player name as defined in the Archipelago multiworld.");

            password = cfg.Bind(
                "Connection",
                "Password",
                "",
                "Room password. Leave blank if the room has no password.");
        }
    }
}
