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

using ContentWarningArchipelago.UI;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    [HarmonyPatch(typeof(MainMenuHandler), "Start")]
    internal static class MainMenuAPPatch
    {
        // ================================================================== Postfix

        /// <summary>
        /// Runs after <c>MainMenuHandler.Start()</c> — once per main-menu load.
        /// Creates the <see cref="APConnectionPanelUI"/> as a top-right overlay on
        /// the main-menu canvas.
        /// </summary>
        private static void Postfix(MainMenuHandler __instance)
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
                    "[MainMenuAPPatch] MainMenuHandler.Start postfix fired — injecting AP panel.");

                // ---- Locate the root Canvas ----
                // UIHandler is a public field on MainMenuHandler.  Walk up its
                // hierarchy to find the nearest Canvas (usually the root).
                Canvas? canvas = null;

                if (__instance.UIHandler != null)
                    canvas = __instance.UIHandler.GetComponentInParent<Canvas>();

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
