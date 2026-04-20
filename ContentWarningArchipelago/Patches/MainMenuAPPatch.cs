// Patches/MainMenuAPPatch.cs
// Injects the Archipelago connection panel into the in-game Escape Menu by
// hooking EscapeMenu.OnEnable — the method called every time the player opens
// the pause/escape menu.
//
// Strategy:
//   [HarmonyPatch(typeof(EscapeMenu), "OnEnable")] fires passively, with zero
//   activity during startup, room-join, or voice-chat initialisation.  The mod
//   is completely silent until the player literally presses Escape for the first
//   time.  At that point __instance.gameObject IS the UI_EscapeMenu object, so
//   InjectPanel receives it directly — no GameObject.Find, no polling, no
//   coroutines, no timers.
//
//   A name-based duplicate guard means InjectPanel creates the panel only on
//   the very first OnEnable call; subsequent Escape-key presses are no-ops.
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
// Multiplayer safety:
//   This is a purely local Unity UI object.  It is never serialised or
//   transmitted over Photon.  ConfigEntry.Value writes only to the local
//   BepInEx .cfg file.  Plugin.Connect() is a local async call with no
//   Photon RPC involvement.

using ContentWarningArchipelago.UI;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    /// <summary>
    /// Harmony postfix on <c>EscapeMenu.OnEnable</c>.
    /// Injects the AP connection panel into the escape-menu hierarchy the first
    /// time the player opens the pause menu.  Completely silent before that point.
    /// </summary>
    [HarmonyPatch(typeof(EscapeMenu), "OnEnable")]
    internal static class MainMenuAPPatch
    {
        // ================================================================== Harmony postfix

        /// <summary>
        /// Runs after <c>EscapeMenu.OnEnable()</c> every time the pause menu
        /// is opened.  The duplicate guard in <see cref="InjectPanel"/> ensures
        /// the panel is only created on the first call.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        private static void Postfix(EscapeMenu __instance)
        {
            // Fast-path: panel already exists from a previous open — do nothing.
            if (GameObject.Find("AP_ConnectionPanel") != null) return;

            Plugin.Logger.LogDebug(
                "[MainMenuAPPatch] EscapeMenu.OnEnable() — injecting AP panel for the first time.");

            InjectPanel(__instance.gameObject);
        }

        // ================================================================== Panel creation

        /// <summary>
        /// Creates the <see cref="APConnectionPanelUI"/> inside the escape menu.
        /// </summary>
        /// <param name="escapeMenu">
        /// The <c>UI_EscapeMenu</c> GameObject received directly from the
        /// <c>EscapeMenu.OnEnable</c> postfix — no <c>GameObject.Find</c> needed.
        /// </param>
        private static void InjectPanel(GameObject escapeMenu)
        {
            try
            {
                Plugin.Logger.LogInfo(
                    "[MainMenuAPPatch] Injecting AP panel into UI_EscapeMenu.");

                // ---- Resolve parent: General > Pause > root ----
                // We prefer a named sub-panel so the AP controls sit alongside the
                // game's own pause-menu sections rather than floating loose at the
                // top of the escape-menu hierarchy.
                Transform parentTransform =
                    escapeMenu.transform.Find("General")
                    ?? escapeMenu.transform.Find("Pause")
                    ?? escapeMenu.transform;

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

                // ---- Activate panel and ensure every ancestor is also active ----
                // If any parent in the hierarchy is inactive the panel will be
                // invisible even though panelGO itself is enabled.  Walk the chain
                // upward and activate any disabled ancestor so the panel is
                // immediately visible when the player opens the Escape menu.
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
