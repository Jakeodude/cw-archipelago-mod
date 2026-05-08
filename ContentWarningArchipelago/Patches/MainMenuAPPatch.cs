// Patches/MainMenuAPPatch.cs
// Injects the Archipelago connection panel directly into the Main Menu title
// screen by hooking MainMenuHandler.Start — the method that runs as soon as
// the NewMainMenuOptimized scene is ready.
//
// Strategy:
//   [HarmonyPatch(typeof(MainMenuHandler), "Start")] Postfix runs immediately
//   after the main menu finishes its own Start(), at which point the
//   MainMenuUIHandler Canvas hierarchy is fully constructed.  The AP panel is
//   parented to the UIHandler so it sits in the same Canvas as the Host/Join
//   buttons and is rendered at world-UI scale without any polling or coroutines.
//
// Placement:
//   The panel is anchored to the top-right corner of the Canvas with a 10 px
//   inset.  SetAsLastSibling() ensures it renders in front of the title-screen
//   background graphics without overlapping the vanilla Host/Join buttons, which
//   are typically centre-screen.
//
// Persistence / Cleanup:
//   The panel lives in NewMainMenuOptimized.  When the player hosts or joins a
//   game, the scene transition to SurfaceScene destroys everything in the main
//   menu automatically — no explicit cleanup required.
//
//   A name-based duplicate guard prevents double-injection on the off-chance
//   Start() is called more than once.
//
// Multiplayer safety:
//   This is a purely local Unity UI object.  It is never serialised or
//   transmitted over Photon.  ConfigEntry.Value writes only to the local
//   BepInEx .cfg file.  Plugin.Connect() is a local async call with no
//   Photon RPC involvement.

using System.Collections;
using ContentWarningArchipelago.UI;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Harmony postfix on <c>MainMenuHandler.Start</c>.
    /// Injects the AP connection panel into the main-menu UI hierarchy the
    /// moment the title screen is ready.
    /// </summary>
    [HarmonyPatch(typeof(MainMenuHandler), "Start")]
    internal static class MainMenuAPPatch
    {
        // ================================================================== Harmony postfix

        /// <summary>
        /// Runs after <c>MainMenuHandler.Start()</c>.
        /// Duplicate guard prevents re-injection if <c>Start</c> fires again.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        private static void Postfix(MainMenuHandler __instance)
        {
            // Fast-path: panel already exists — do nothing.
            if (GameObject.Find("AP_ConnectionPanel") != null) return;

            Plugin.Logger.LogDebug(
                "[MainMenuAPPatch] MainMenuHandler.Start() completed — " +
                "starting 3-second coroutine before injecting AP panel.");

            // Delay injection by 3 seconds so that Photon Voice has fully
            // initialised before we modify the scene hierarchy.  Injecting
            // synchronously inside MainMenuHandler.Start() races with the
            // Voice init and (a) silences audio and (b) leaves the
            // ConnectionStateHandler state machine in a Busy-like state that
            // prevents joining players from waking up / opening the house door.
            __instance.StartCoroutine(InjectPanelDelayed(__instance));
        }

        // ================================================================== Coroutine

        /// <summary>
        /// Waits 3 seconds for Photon Voice to complete initialisation, then
        /// injects the AP panel.  A duplicate guard is re-checked after the
        /// wait in case another code path already injected the panel.
        /// </summary>
        private static IEnumerator InjectPanelDelayed(MainMenuHandler menu)
        {
            yield return new WaitForSeconds(3f);

            // Re-check after the wait — another Start() call or a scene reload
            // may have already injected the panel.
            if (GameObject.Find("AP_ConnectionPanel") != null) yield break;

            InjectPanel(menu);
        }

        // ================================================================== Panel creation

        /// <summary>
        /// Creates the <see cref="APConnectionPanelUI"/> inside the main-menu
        /// UI hierarchy.
        /// </summary>
        /// <param name="menu">
        /// The <c>MainMenuHandler</c> instance received directly from the
        /// postfix — no <c>GameObject.Find</c> needed.
        /// </param>
        private static void InjectPanel(MainMenuHandler menu)
        {
            try
            {
                Plugin.Logger.LogInfo(
                    "[MainMenuAPPatch] Injecting AP panel into main-menu UI.");

                // ---- Choose parent ----
                // Prefer UIHandler (the Canvas-backed UIPageHandler that owns the
                // Host/Join buttons) so the panel inherits the same Canvas scaler.
                // Fall back to the MainMenuHandler GameObject itself if UIHandler
                // is somehow null.
                Transform parentTransform =
                    (menu.UIHandler != null ? menu.UIHandler.transform : menu.transform);

                Plugin.Logger.LogDebug(
                    $"[MainMenuAPPatch] Using parent: {parentTransform.name} " +
                    $"(full path: {GetPath(parentTransform)})");

                // ---- Create the panel GameObject ----
                var panelGO = new GameObject("AP_ConnectionPanel");
                panelGO.transform.SetParent(parentTransform, worldPositionStays: false);

                // ---- Explicit scale reset ----
                // Ensures the panel is not affected by any inherited world-space
                // scaling on the parent transform.
                panelGO.transform.localScale = Vector3.one;

                // ---- RectTransform: top-right inset ----
                // Anchor + pivot both at (1, 1) = top-right corner.
                // anchoredPosition (-10, -10) = 10 px inset from the edges.
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

                // ---- Activate panel and ensure every ancestor is also active ----
                // Walk the parent chain and enable any inactive ancestor so the
                // panel is immediately visible on the title screen.
                panelGO.SetActive(true);
                Transform ancestor = parentTransform;
                while (ancestor != null)
                {
                    if (!ancestor.gameObject.activeSelf)
                    {
                        Plugin.Logger.LogDebug(
                            $"[MainMenuAPPatch] Activating inactive ancestor: {GetPath(ancestor)}");
                        ancestor.gameObject.SetActive(true);
                    }
                    ancestor = ancestor.parent;
                }

                // ---- Render order: in front of title graphics ----
                // SetAsLastSibling places the panel at the highest sibling index,
                // so it is drawn on top of the background and title-screen art
                // while leaving the Host/Join buttons (which are centre-screen)
                // unobstructed.
                panelGO.transform.SetAsLastSibling();

                Plugin.Logger.LogInfo(
                    "[MainMenuAPPatch] AP connection panel successfully injected into main-menu UI.");
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
