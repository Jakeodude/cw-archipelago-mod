// Patches/ModManagerAPPatch.cs
// Injects the Archipelago connection panel into the in-game Mod Manager UI.
//
// Strategy: Manual Harmony patch using AccessTools.TypeByName("ModManagerUI").
//
//   ModManagerUI lives in a separate assembly (the game's built-in mod manager
//   plugin) that is NOT part of Assembly-CSharp.dll, so we cannot reference it
//   at compile time with typeof(ModManagerUI).  Instead, we resolve the type at
//   runtime via AccessTools and apply the Harmony postfix manually.
//
//   This means the patch is applied explicitly from Plugin.Awake() rather than
//   being picked up by harmony.PatchAll().  It also gracefully no-ops if the
//   Mod Manager isn't present in a given build of the game.
//
// Placement:
//   The panel GameObject is parented to modlist.transform (resolved via
//   reflection) and placed at the bottom with SetAsLastSibling().  We patch
//   OnEnable (not Awake) so the panel is re-injected every time the Mod Manager
//   tab is opened, and a scene-wide GameObject.Find guard prevents duplicates.
//
// Multiplayer safety:
//   This is a local-only Unity UI object. It is never serialised or sent over
//   Photon.  ConfigEntry.Value writes only to the local BepInEx .cfg file.
//   Plugin.Connect() is a local async call with no Photon RPC involvement.

using ContentWarningArchipelago.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ContentWarningArchipelago.Patches
{
    internal static class ModManagerAPPatch
    {
        // ================================================================== Registration

        /// <summary>
        /// Called from <see cref="Plugin.Awake"/> after <c>harmony.PatchAll()</c>.
        /// Resolves <c>ModManagerUI</c> at runtime and applies a postfix to its
        /// <c>OnEnable()</c> method.  Logs a warning and exits cleanly if the type
        /// cannot be found.
        /// </summary>
        internal static void TryApplyPatch(Harmony harmony)
        {
            // Resolve the type at runtime — it lives in a separate assembly that
            // we cannot reference at compile time.
            var modManagerType = AccessTools.TypeByName("ModManagerUI");
            if (modManagerType == null)
            {
                Plugin.Logger.LogWarning(
                    "[ModManagerAPPatch] ModManagerUI type not found in any loaded assembly. " +
                    "The AP connection panel will not be injected into the Mod Manager. " +
                    "This is expected if the game's built-in Mod Manager is absent.");
                return;
            }

            var onEnableMethod = AccessTools.Method(modManagerType, "OnEnable");
            if (onEnableMethod == null)
            {
                Plugin.Logger.LogWarning(
                    "[ModManagerAPPatch] Could not find ModManagerUI.OnEnable() — skipping patch.");
                return;
            }

            var postfix = new HarmonyMethod(
                typeof(ModManagerAPPatch),
                nameof(OnEnable_Postfix));

            harmony.Patch(onEnableMethod, postfix: postfix);
            Plugin.Logger.LogInfo(
                "[ModManagerAPPatch] Successfully patched ModManagerUI.OnEnable() with AP panel injector.");
        }

        // ================================================================== Postfix

        /// <summary>
        /// Postfix injected into <c>ModManagerUI.OnEnable()</c>.
        /// <para>
        /// <c>__instance</c> is typed as <see cref="MonoBehaviour"/> because we cannot
        /// reference <c>ModManagerUI</c> at compile time.  All operations we need
        /// (transform hierarchy, reflection field access) are available on
        /// <see cref="MonoBehaviour"/> / <see cref="Component"/> / <see cref="GameObject"/>.
        /// </para>
        /// </summary>
        private static void OnEnable_Postfix(MonoBehaviour __instance)
        {
            try
            {
                // Confirm the patch is firing — visible in the BepInEx console each
                // time the Mod Manager tab is opened.
                Plugin.Logger.LogInfo(
                    "[ModManagerAPPatch] OnEnable_Postfix fired — checking for existing AP panel.");

                // Guard: scene-wide search prevents duplicates across tab re-opens.
                if (GameObject.Find("AP_ConnectionPanel") != null)
                {
                    Plugin.Logger.LogDebug(
                        "[ModManagerAPPatch] AP_ConnectionPanel already exists in scene — skipping.");
                    return;
                }

                // ---- Resolve modlist transform via reflection ----
                // We target modlist.transform so our panel sits inside the scroll list,
                // below the mod entries.  RefreshList() only destroys children it creates
                // itself (it calls Instantiate, so it tracks its own entries); our
                // named panel will survive unless the whole modlist is destroyed.
                Transform parentTransform = __instance.transform; // fallback
                var modlistField = AccessTools.Field(__instance.GetType(), "modlist");
                if (modlistField != null)
                {
                    var modlistObj = modlistField.GetValue(__instance) as UnityEngine.Object;
                    if (modlistObj is Component modlistComponent)
                        parentTransform = modlistComponent.transform;
                    else if (modlistObj is GameObject modlistGO)
                        parentTransform = modlistGO.transform;
                    else
                        Plugin.Logger.LogWarning(
                            "[ModManagerAPPatch] 'modlist' field found but could not be cast to Component or GameObject — falling back to root transform.");
                }
                else
                {
                    Plugin.Logger.LogWarning(
                        "[ModManagerAPPatch] Could not find 'modlist' field on ModManagerUI — falling back to root transform.");
                }

                // ---- Create panel GameObject ----
                var panelGO = new GameObject("AP_ConnectionPanel");

                // Parent to modlist (or root fallback).
                panelGO.transform.SetParent(parentTransform, worldPositionStays: false);

                // LayoutElement lets the panel participate in any VerticalLayoutGroup
                // on the parent.
                var le             = panelGO.AddComponent<LayoutElement>();
                le.preferredWidth  = -1f;   // -1 = fill parent width
                le.preferredHeight = 155f;  // header + 3 field rows + button row

                // Attach panel behaviour — it builds its own child UI in its own Awake().
                panelGO.AddComponent<APConnectionPanelUI>();

                // Place at the very bottom of the list (after all mod entries).
                panelGO.transform.SetAsLastSibling();

                Plugin.Logger.LogInfo(
                    "[ModManagerAPPatch] AP connection panel successfully injected into ModManagerUI.");
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogError($"[ModManagerAPPatch] Exception while injecting AP panel: {ex}");
            }
        }
    }
}
