// Patches/MainMenuAPPatch.cs
// Injects the Archipelago connection panel into the main menu by postfixing
// MainMenuHandler.Start().
//
// Strategy:
//   MainMenuHandler lives in Assembly-CSharp, so we can use typeof(MainMenuHandler)
//   at compile time with a standard [HarmonyPatch] attribute — no runtime
//   AccessTools.TypeByName gymnastics needed.
//
//   harmony.PatchAll() (called in Plugin.Awake) picks this patch up automatically.
//
// Placement:
//   The panel is parented directly to the root Canvas of the main menu, then
//   anchored to the top-right corner so it floats above all menu pages and is
//   always visible regardless of which tab is open.
//
// Persistence / Cleanup:
//   The panel lives in the NewMainMenuOptimized scene.  When the player starts a
//   game, PhotonNetwork.LoadLevel("SurfaceScene") triggers a full scene transition
//   which destroys every non-DontDestroyOnLoad object — including the canvas and
//   our panel — automatically.  No explicit cleanup is required.
//
//   A name-based duplicate guard prevents double-injection if Start() is somehow
//   called more than once or the player returns to the menu.
//
// Multiplayer safety:
//   This is a purely local Unity UI object.  It is never serialised or transmitted
//   over Photon.  ConfigEntry.Value writes only to the local BepInEx .cfg file.
//   Plugin.Connect() is a local async call with no Photon RPC involvement.
//
// Deferred injection:
//   The panel is NOT injected immediately in Start().  Instead a coroutine waits
//   until PhotonNetwork.InRoom is true before calling InjectPanel(), preventing
//   interference with the initial voice-chat setup and door-sync RPCs that Photon
//   dispatches as part of the room-join handshake.

using System.Collections;
using ContentWarningArchipelago.UI;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    [HarmonyPatch(typeof(MainMenuHandler), "Start")]
    internal static class MainMenuAPPatch
    {
        // ================================================================== Postfix

        /// <summary>
        /// Runs after <c>MainMenuHandler.Start()</c> — once per main-menu load.
        /// Defers the actual panel creation until <c>PhotonNetwork.InRoom</c> is
        /// <c>true</c> so we don't interfere with the initial voice-chat connection
        /// and door-sync RPCs that fire during the Photon room-join handshake.
        /// </summary>
        private static void Postfix(MainMenuHandler __instance)
        {
            Plugin.Instance.StartCoroutine(WaitForRoomThenInject(__instance));
        }

        // ================================================================== Coroutine

        /// <summary>
        /// Yields until the local client has joined a Photon room, then injects
        /// the AP connection panel.  If <c>MainMenuHandler</c> is destroyed before
        /// the room is joined (e.g. the player started a game), the coroutine exits
        /// silently without injecting.
        /// </summary>
        private static IEnumerator WaitForRoomThenInject(MainMenuHandler instance)
        {
            // Wait until Photon room state is established so that the initial
            // voice-chat setup and door-sync RPCs have already been dispatched.
            yield return new WaitUntil(() => PhotonNetwork.InRoom);

            // Guard: MainMenuHandler may have been destroyed if a scene transition
            // happened while we were waiting (e.g. auto-joining a game in progress).
            if (instance == null)
            {
                Plugin.Logger.LogDebug(
                    "[MainMenuAPPatch] MainMenuHandler was destroyed before InRoom — skipping panel inject.");
                yield break;
            }

            InjectPanel(instance);
        }

        // ================================================================== Panel creation

        /// <summary>
        /// Creates the <see cref="APConnectionPanelUI"/> as a top-right overlay on
        /// the main-menu canvas.  Called only after <c>PhotonNetwork.InRoom</c> is true.
        /// </summary>
        private static void InjectPanel(MainMenuHandler instance)
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
                    "[MainMenuAPPatch] PhotonNetwork.InRoom confirmed — injecting AP panel.");

                // ---- Locate the root Canvas ----
                // UIHandler is a public field on MainMenuHandler.  Walk up its
                // hierarchy to find the nearest Canvas (usually the root).
                Canvas? canvas = null;

                if (instance.UIHandler != null)
                    canvas = instance.UIHandler.GetComponentInParent<Canvas>();

                // Fallback: search the whole scene for any active canvas.
                if (canvas == null)
                    canvas = Object.FindObjectOfType<Canvas>();

                if (canvas == null)
                {
                    Plugin.Logger.LogError(
                        "[MainMenuAPPatch] Could not find a Canvas in the main menu scene. " +
                        "AP panel will not be injected.");
                    return;
                }

                Plugin.Logger.LogDebug(
                    $"[MainMenuAPPatch] Using canvas: {canvas.name}");

                // ---- Create the panel GameObject ----
                var panelGO = new GameObject("AP_ConnectionPanel");

                // Parent directly to the canvas so the panel is independent of any
                // page transitions.
                panelGO.transform.SetParent(canvas.transform, worldPositionStays: false);

                // ---- Position: top-right corner ----
                // Anchor + pivot both at (1, 1) = top-right.
                // anchoredPosition (-10, -10) = 10 px inset from the top and right edges.
                // sizeDelta.x fixes the width; ContentSizeFitter (added by APConnectionPanelUI)
                // drives the height automatically.
                var rect        = panelGO.GetComponent<RectTransform>()
                                  ?? panelGO.AddComponent<RectTransform>();
                rect.anchorMin  = new Vector2(1f, 1f);
                rect.anchorMax  = new Vector2(1f, 1f);
                rect.pivot      = new Vector2(1f, 1f);
                rect.anchoredPosition = new Vector2(-10f, -10f);
                rect.sizeDelta  = new Vector2(350f, 0f);   // height auto via CSF

                // ---- Attach the panel behaviour ----
                // APConnectionPanelUI.Awake() builds all child UI programmatically.
                panelGO.AddComponent<APConnectionPanelUI>();

                // Render on top of all menu pages.
                panelGO.transform.SetAsLastSibling();

                Plugin.Logger.LogInfo(
                    "[MainMenuAPPatch] AP connection panel successfully injected into main menu canvas.");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError(
                    $"[MainMenuAPPatch] Exception while injecting AP panel: {ex}");
            }
        }
    }
}
