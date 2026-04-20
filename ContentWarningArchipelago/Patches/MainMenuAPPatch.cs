// Patches/MainMenuAPPatch.cs
// Injects the Archipelago connection panel into the in-game Escape Menu by
// locating UI_EscapeMenu once the local Player object has awoken.
//
// Strategy:
//   A [HarmonyPatch(typeof(Player), "Awake")] Postfix fires after the game's
//   own Player.Awake() body has run — meaning Photon voice initialisation and
//   all room-join RPCs are already complete.  We check __instance.IsLocal so
//   the injection only happens once (for the owning client) and is silently
//   skipped for every remote player object that Photon spawns.
//
//   Because Player is spawned into the SurfaceScene, UI_EscapeMenu is already
//   present in the hierarchy by the time this postfix fires, so
//   GameObject.Find("UI_EscapeMenu") succeeds immediately with no polling or
//   coroutine needed.
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
//   A name-based duplicate guard prevents double-injection if the postfix is
//   somehow called more than once per session.
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
    /// Harmony postfix on <c>Player.Awake</c>.
    /// Injects the AP connection panel into <c>UI_EscapeMenu</c> exactly once,
    /// immediately after the local player object has finished its own Awake
    /// (which includes voice-chat initialisation).
    /// </summary>
    [HarmonyPatch(typeof(Player), "Awake")]
    internal static class MainMenuAPPatch
    {
        // ================================================================== Harmony postfix

        /// <summary>
        /// Runs after <c>Player.Awake()</c> on every spawned Player instance.
        /// The <c>IsLocal</c> guard ensures we only inject for the owning client.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        private static void Postfix(Player __instance)
        {
            if (!__instance.IsLocal) return;

            Plugin.Logger.LogDebug(
                "[MainMenuAPPatch] Local Player.Awake() completed — injecting AP panel.");

            InjectPanel();
        }

        // ================================================================== Panel creation

        /// <summary>
        /// Creates the <see cref="APConnectionPanelUI"/> inside <c>UI_EscapeMenu</c>.
        /// Called only from the <c>Player.Awake</c> postfix after the local player
        /// object (and its voice-chat handlers) have finished initialising.
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
                // By the time Player.Awake() fires, SurfaceScene is fully loaded and
                // UI_EscapeMenu is present in the hierarchy.
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
