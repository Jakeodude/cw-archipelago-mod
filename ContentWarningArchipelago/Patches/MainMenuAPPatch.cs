// Patches/MainMenuAPPatch.cs
// Injects the Archipelago connection panel into the in-game Escape Menu by
// locating UI_EscapeMenu once the local client has joined a Photon room.
//
// Strategy:
//   This patch no longer hooks MainMenuHandler.Start.  Instead, Plugin.Start()
//   calls MainMenuAPPatch.TryInject() which launches a coroutine that waits
//   until PhotonNetwork.InRoom before searching for UI_EscapeMenu in the loaded
//   SurfaceScene.  This avoids any interference with the initial voice-chat
//   setup and door-sync RPCs that Photon dispatches during the room-join
//   handshake.
//
// Placement:
//   The panel is parented to the first matching child of UI_EscapeMenu —
//   preferring "General", then "Pause", falling back to the root object.
//   SetAsFirstSibling() places it above the Quit Game buttons in the sibling
//   list so it never overlaps them.
//
// Persistence / Cleanup:
//   The panel lives in SurfaceScene.  A full scene transition back to
//   NewMainMenuOptimized destroys every non-DontDestroyOnLoad object —
//   including UI_EscapeMenu and our panel — automatically.  No explicit
//   cleanup is required.
//
//   A name-based duplicate guard prevents double-injection if InjectPanel is
//   somehow called more than once.
//
// Multiplayer safety:
//   This is a purely local Unity UI object.  It is never serialised or
//   transmitted over Photon.  ConfigEntry.Value writes only to the local
//   BepInEx .cfg file.  Plugin.Connect() is a local async call with no
//   Photon RPC involvement.

using System.Collections;
using ContentWarningArchipelago.UI;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    // NOTE: No [HarmonyPatch] attribute — this class is no longer a Harmony
    // postfix on MainMenuHandler.Start.  Plugin.Start() calls TryInject()
    // directly to launch the deferred-injection coroutine.
    internal static class MainMenuAPPatch
    {
        // ================================================================== Entry point

        /// <summary>
        /// Called once from <see cref="Plugin.Start"/> to kick off the deferred
        /// injection coroutine.
        /// </summary>
        internal static void TryInject()
        {
            Plugin.Instance.StartCoroutine(WaitForRoomThenInject());
        }

        // ================================================================== Coroutine

        /// <summary>
        /// Yields until the local client has joined a Photon room, then injects
        /// the AP connection panel into <c>UI_EscapeMenu</c>.
        /// </summary>
        private static IEnumerator WaitForRoomThenInject()
        {
            // Wait until Photon room state is established so that the initial
            // voice-chat setup and door-sync RPCs have already been dispatched.
            yield return new WaitUntil(() => PhotonNetwork.InRoom);

            Plugin.Logger.LogDebug(
                "[MainMenuAPPatch] PhotonNetwork.InRoom confirmed — beginning panel inject.");

            InjectPanel();
        }

        // ================================================================== Panel creation

        /// <summary>
        /// Creates the <see cref="APConnectionPanelUI"/> inside <c>UI_EscapeMenu</c>.
        /// Called only after <c>PhotonNetwork.InRoom</c> is <c>true</c>.
        /// </summary>
        private static void InjectPanel()
        {
            try
            {
                // ---- Duplicate guard ----
                if (GameObject.Find("AP_ConnectionPanel") != null)
                {
                    Plugin.Logger.LogDebug(
                        "[MainMenuAPPatch] AP_ConnectionPanel already exists — skipping.");
                    return;
                }

                Plugin.Logger.LogInfo(
                    "[MainMenuAPPatch] Injecting AP panel into UI_EscapeMenu.");

                // ---- Locate UI_EscapeMenu ----
                var escapeMenuGO = GameObject.Find("UI_EscapeMenu");
                if (escapeMenuGO == null)
                {
                    Plugin.Logger.LogError(
                        "[MainMenuAPPatch] Could not find UI_EscapeMenu in the scene. " +
                        "AP panel will not be injected.");
                    return;
                }

                // ---- Resolve parent: General > Pause > root ----
                // We prefer a named sub-panel so the AP controls sit alongside the
                // game's own pause-menu sections rather than floating loose at the
                // top of the escape-menu hierarchy.
                Transform parentTransform =
                    escapeMenuGO.transform.Find("General")
                    ?? escapeMenuGO.transform.Find("Pause")
                    ?? escapeMenuGO.transform;

                Plugin.Logger.LogDebug(
                    $"[MainMenuAPPatch] Using parent: {parentTransform.name} " +
                    $"(full path: {GetPath(parentTransform)})");

                // ---- Create the panel GameObject ----
                var panelGO = new GameObject("AP_ConnectionPanel");
                panelGO.transform.SetParent(parentTransform, worldPositionStays: false);

                // ---- RectTransform: top-right inset ----
                // Anchor + pivot both at (1, 1) = top-right.
                // anchoredPosition (-10, -10) = 10 px inset from the top-right edges.
                // sizeDelta.x fixes the width; ContentSizeFitter (added by
                // APConnectionPanelUI) drives the height automatically.
                var rect       = panelGO.GetComponent<RectTransform>()
                                 ?? panelGO.AddComponent<RectTransform>();
                rect.anchorMin = new Vector2(1f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot     = new Vector2(1f, 1f);
                rect.anchoredPosition = new Vector2(-10f, -10f);
                rect.sizeDelta = new Vector2(350f, 0f);   // height auto via CSF

                // ---- Attach the panel behaviour ----
                // APConnectionPanelUI.Awake() builds all child UI programmatically.
                panelGO.AddComponent<APConnectionPanelUI>();

                // ---- Activate ----
                panelGO.SetActive(true);

                // ---- Layer: before Quit Game buttons ----
                // SetAsFirstSibling places the panel at sibling-index 0 within the
                // chosen parent, so it appears before the game's own action buttons
                // (including Quit Game) and does not visually overlap them.
                panelGO.transform.SetAsFirstSibling();

                Plugin.Logger.LogInfo(
                    "[MainMenuAPPatch] AP connection panel successfully injected into UI_EscapeMenu.");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[MainMenuAPPatch] Exception while injecting AP panel: {ex}");
            }
        }

        // ================================================================== Helpers

        /// <summary>Returns the full scene-hierarchy path of a transform for debug logs.</summary>
        private static string GetPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return GetPath(t.parent) + "/" + t.name;
        }
    }
}
