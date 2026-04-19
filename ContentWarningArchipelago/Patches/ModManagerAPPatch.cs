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
//   The panel GameObject is parented to the ModManagerUI root transform (NOT to
//   modlist.transform), so ModManagerUI.RefreshList() — which only destroys
//   children of modlist — can never remove our panel.
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
        /// <c>Awake()</c> method.  Logs a warning and exits cleanly if the type
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

            var awakeMethod = AccessTools.Method(modManagerType, "Awake");
            if (awakeMethod == null)
            {
                Plugin.Logger.LogWarning(
                    "[ModManagerAPPatch] Could not find ModManagerUI.Awake() — skipping patch.");
                return;
            }

            var postfix = new HarmonyMethod(
                typeof(ModManagerAPPatch),
                nameof(Awake_Postfix));

            harmony.Patch(awakeMethod, postfix: postfix);
            Plugin.Logger.LogInfo(
                "[ModManagerAPPatch] Successfully patched ModManagerUI.Awake() with AP panel injector.");
        }

        // ================================================================== Postfix

        /// <summary>
        /// Postfix injected into <c>ModManagerUI.Awake()</c>.
        /// <para>
        /// <c>__instance</c> is typed as <see cref="MonoBehaviour"/> because we cannot
        /// reference <c>ModManagerUI</c> at compile time.  All operations we need
        /// (transform hierarchy, <c>GetComponentInChildren</c>) are available on
        /// <see cref="MonoBehaviour"/> / <see cref="Component"/> / <see cref="GameObject"/>.
        /// </para>
        /// </summary>
        private static void Awake_Postfix(MonoBehaviour __instance)
        {
            try
            {
                // Guard: inject only once per ModManagerUI instance.
                if (__instance.GetComponentInChildren<APConnectionPanelUI>(includeInactive: true) != null)
                {
                    Plugin.Logger.LogDebug(
                        "[ModManagerAPPatch] AP panel already present on this ModManagerUI — skipping.");
                    return;
                }

                // ---- Create panel GameObject ----
                var panelGO = new GameObject("AP_ConnectionPanel");

                // Parent to the ModManagerUI root (NOT to modlist), so that
                // ModManagerUI.RefreshList() — which only clears modlist's children —
                // cannot destroy our panel.
                panelGO.transform.SetParent(__instance.transform, worldPositionStays: false);

                // LayoutElement lets the panel participate in any VerticalLayoutGroup
                // that may exist on the ModManagerUI root.
                var le             = panelGO.AddComponent<LayoutElement>();
                le.preferredWidth  = -1f;   // -1 = fill parent width
                le.preferredHeight = 155f;  // header + 3 field rows + button row

                // Attach panel behaviour — it builds its own child UI in its own Awake().
                panelGO.AddComponent<APConnectionPanelUI>();

                // Place at the bottom of the Mod Manager (after the mod list).
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
