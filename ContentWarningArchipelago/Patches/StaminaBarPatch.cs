// Patches/StaminaBarPatch.cs
// Normalises the stamina HUD bar so it correctly reflects currentStamina /
// maxStamina at any cap value.
//
// Vanilla bug: UI_Stamina.LateUpdate computes
//   fill.fillAmount = currentStamina * (maxStamina * 0.01f)
// which is currentStamina × maxStamina / 100 — NOT currentStamina / maxStamina.
// It only happens to give a correct 0-1 fill range when maxStamina ≈ 10 (the
// vanilla prefab default).  Once Progressive Stamina raises maxStamina above
// ~10, the bar stays clamped at 1.0 across most of the range and only starts
// dropping near empty, hiding the extra capacity from the player.
//
// Fix: Prefix UI_Stamina.LateUpdate with the correct formula and skip the
// vanilla method.  Applied unconditionally — the corrected formula produces
// identical output to the buggy one when maxStamina = 10, so non-upgraded
// play is unaffected.
//
// CurvedUI: the vanilla method also calls m_curvedEffect.TryUpdateCurvedVertex().
// We preserve that via reflection so we don't have to add a CurvedUI assembly
// reference to the project.

using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ContentWarningArchipelago.Patches
{
    [HarmonyPatch(typeof(UI_Stamina), "LateUpdate")]
    internal static class StaminaBarPatch
    {
        // Cached reflection handles for the private CurvedUI hook.
        private static FieldInfo? _curvedEffectField;
        private static MethodInfo? _tryUpdateMethod;
        private static bool _curvedLookupTried;

        [HarmonyPrefix]
        private static bool Prefix(UI_Stamina __instance)
        {
            if ((object)Player.localPlayer == null) return false;

            var ctrl = Player.localPlayer.refs?.controller;
            if ((object)ctrl == null || ctrl.maxStamina <= 0f) return false;

            __instance.fill.fillAmount = Mathf.Clamp01(
                Player.localPlayer.data.currentStamina / ctrl.maxStamina);

            RefreshCurvedEffect(__instance);

            return false; // skip original
        }

        // Mirror the vanilla `if ((bool)m_curvedEffect) m_curvedEffect.TryUpdateCurvedVertex();`
        // call via reflection so we don't have to reference the CurvedUI assembly directly.
        private static void RefreshCurvedEffect(UI_Stamina instance)
        {
            if (!_curvedLookupTried)
            {
                _curvedLookupTried = true;
                _curvedEffectField = AccessTools.Field(typeof(UI_Stamina), "m_curvedEffect");
            }

            var effect = _curvedEffectField?.GetValue(instance);
            if (effect == null) return;

            if (_tryUpdateMethod == null)
                _tryUpdateMethod = AccessTools.Method(effect.GetType(), "TryUpdateCurvedVertex");

            _tryUpdateMethod?.Invoke(effect, null);
        }
    }
}
