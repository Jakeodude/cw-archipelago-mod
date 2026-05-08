// UI/APNotificationUI.cs
// Native HUD notification helper for Archipelago item events.
//
// Strategy (matching the game's own MetaProgressionHandler / SurfaceNetworkHandler
// notification calls):
//   • ALL incoming item notifications  → UserInterface.ShowMoneyNotification with
//     MoneyCellUI.MoneyCellType.MetaCoins  (chime sound, green styling)
//   • Outgoing location-check notifications → UserInterface.ShowMoneyNotification
//     with MoneyCellUI.MoneyCellType.Revenue  (cash-register sound, green styling)
//
//   Both methods fall back to SpawnHUDLabel if ShowMoneyNotification throws
//   (e.g. UI not yet initialised on the main menu).
//
// ShowMoneyNotification(string header, string money, MoneyCellType cellType)
//   header  → displayed in m_messageText  (top / larger line)
//   money   → displayed in m_moneyText    (bottom / value line)
//
// NOTE ON NULLABLE: Unity MonoBehaviours override the == null operator, so C# 8
// nullable reference types (TextMeshProUGUI?) conflict with Unity's operator.
// We therefore use `= null!` (null-forgiving assignment) and check via the
// Unity-standard `obj == null` pattern.

using System.Collections;
using TMPro;
using UnityEngine;

namespace ContentWarningArchipelago.UI
{
    /// <summary>
    /// MonoBehaviour that drives the fade-in / hold / fade-out coroutine for a
    /// single notification label.  Attached to the spawned label GameObject so
    /// Unity owns the coroutine lifetime.
    /// </summary>
    internal class APNotificationDriver : MonoBehaviour
    {
        // Use = null! to satisfy the compiler under <Nullable>enable</Nullable>
        // while still allowing Unity's null-override to function correctly.
        internal TextMeshProUGUI label = null!;

        internal void Show(string line1, string line2, float duration = 3.5f)
        {
            StartCoroutine(AnimateRoutine(line1, line2, duration));
        }

        private IEnumerator AnimateRoutine(string line1, string line2, float duration)
        {
            // Unity-safe null check (uses Unity's == operator override).
            if ((object)label == null) yield break;

            label.text    = string.IsNullOrEmpty(line2)
                ? line1
                : $"{line1}\n<size=70%>{line2}</size>";
            label.color   = new Color(1f, 0.92f, 0.016f, 0f); // gold, fully transparent
            label.enabled = true;

            // --- Fade in (0.3 s) ---
            float elapsed = 0f;
            while (elapsed < 0.3f)
            {
                elapsed += Time.deltaTime;
                Color c = label.color;
                label.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(elapsed / 0.3f));
                yield return null;
            }
            label.color = new Color(label.color.r, label.color.g, label.color.b, 1f);

            // --- Hold ---
            float holdTime = Mathf.Max(0f, duration - 0.3f - 0.6f);
            yield return new WaitForSeconds(holdTime);

            // --- Fade out (0.6 s) ---
            elapsed = 0f;
            while (elapsed < 0.6f)
            {
                elapsed += Time.deltaTime;
                Color c = label.color;
                label.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(1f - elapsed / 0.6f));
                yield return null;
            }

            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Static façade — call <see cref="ShowItemReceived"/> or
    /// <see cref="ShowMoneyReceived"/> from <c>ItemData.HandleReceivedItem</c>.
    /// </summary>
    public static class APNotificationUI
    {
        private const float Duration = 3.5f;

        // ================================================================== public API

        /// <summary>
        /// Show a native "Received: [Item Name]" popup for any incoming Archipelago
        /// item (upgrades, unlocks, traps, currency, etc.).
        ///
        /// Uses <c>UserInterface.ShowMoneyNotification</c> with
        /// <c>MoneyCellUI.MoneyCellType.MetaCoins</c> so the popup renders with
        /// the same chime sound and green styling as the Meta Coins HUD element.
        ///
        ///   header (m_messageText) = "Received: {itemName}"
        ///   money  (m_moneyText)   = "from {senderName}"  — empty when self-sent
        ///
        /// Falls back to <see cref="SpawnHUDLabel"/> if the game method throws
        /// (e.g. the UI singleton is not yet loaded on the main menu).
        /// </summary>
        /// <param name="itemName">Friendly display name of the item.</param>
        /// <param name="senderName">AP slot name of the player who sent it.
        /// Pass empty string when the item is from the local player's own world.</param>
        public static void ShowItemReceived(string itemName, string senderName = "")
        {
            string header = $"Received: {itemName}";
            string money  = string.IsNullOrEmpty(senderName) ? "" : $"from {senderName}";

            try
            {
                UserInterface.ShowMoneyNotification(header, money, MoneyCellUI.MoneyCellType.MetaCoins);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[APNotif] ShowMoneyNotification (MetaCoins) failed: {ex.Message} — using fallback label.");
                SpawnHUDLabel(header, money);
            }
        }

        /// <summary>
        /// Show a native "Location Found!" popup when a check is sent to the
        /// Archipelago server.
        ///
        /// Uses <c>UserInterface.ShowMoneyNotification</c> with
        /// <c>MoneyCellUI.MoneyCellType.Revenue</c> so the popup renders with
        /// the cash-register sound and green styling, distinct from incoming items.
        ///
        ///   header (m_messageText) = "Location Found!"
        ///   money  (m_moneyText)   = the AP location name
        ///
        /// Falls back to <see cref="SpawnHUDLabel"/> if the game method throws.
        /// </summary>
        /// <param name="locationName">The AP location name that was checked.</param>
        public static void ShowLocationFound(string locationName)
        {
            try
            {
                UserInterface.ShowMoneyNotification("Location Found!", locationName, MoneyCellUI.MoneyCellType.Revenue);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[APNotif] ShowMoneyNotification (Revenue) failed: {ex.Message} — using fallback label.");
                SpawnHUDLabel("Location Found!", locationName);
            }
        }

        /// <summary>
        /// Show a currency notification.
        /// Delegates to the game's built-in <c>UserInterface.ShowMoneyNotification</c>
        /// (same call used by <c>MetaProgressionHandler.AddMetaCoins</c>) so the
        /// popup uses the correct icon and styling.
        /// Falls back to <see cref="SpawnHUDLabel"/> if the game method throws.
        /// </summary>
        /// <param name="displayLabel">Label shown in the popup (e.g. "Meta Coins Received").</param>
        /// <param name="amount">Numeric amount to display.</param>
        /// <param name="cellType">Icon type (<c>MetaCoins</c> or <c>Money</c>).</param>
        public static void ShowMoneyReceived(
            string displayLabel,
            int amount,
            MoneyCellUI.MoneyCellType cellType)
        {
            try
            {
                UserInterface.ShowMoneyNotification(displayLabel, amount.ToString(), cellType);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[APNotif] UserInterface.ShowMoneyNotification failed: {ex.Message}");
                SpawnHUDLabel("[AP] Currency Received!", $"+{amount}  {displayLabel}");
            }
        }

        // ================================================================== HUD label spawner

        /// <summary>
        /// Finds the player's HUD canvas and spawns a temporary TMPro label
        /// driven by <see cref="APNotificationDriver"/>.
        /// </summary>
        private static void SpawnHUDLabel(string line1, string line2)
        {
            try
            {
                Canvas hudCanvas = FindHUDCanvas();
                if ((object)hudCanvas == null)
                {
                    Plugin.Logger.LogDebug(
                        $"[APNotif] No HUD canvas — logging instead: {line1} | {line2}");
                    return;
                }

                var go = new GameObject("AP_Notification");
                go.transform.SetParent(hudCanvas.transform, false);

                var rt            = go.AddComponent<RectTransform>();
                rt.anchorMin      = new Vector2(0.5f, 1f);
                rt.anchorMax      = new Vector2(0.5f, 1f);
                rt.pivot          = new Vector2(0.5f, 1f);
                rt.anchoredPosition = new Vector2(0f, -120f);
                rt.sizeDelta      = new Vector2(700f, 80f);

                var tmp             = go.AddComponent<TextMeshProUGUI>();
                tmp.alignment       = TextAlignmentOptions.Center;
                tmp.fontSize        = 26f;
                tmp.fontStyle       = FontStyles.Bold;
                tmp.raycastTarget   = false;
                tmp.enabled         = false; // APNotificationDriver enables it

                var driver  = go.AddComponent<APNotificationDriver>();
                driver.label = tmp;
                driver.Show(line1, line2, Duration);
            }
            catch (System.Exception ex)
            {
                Plugin.Logger.LogWarning($"[APNotif] SpawnHUDLabel failed: {ex.Message}");
            }
        }

        // ================================================================== canvas search

        /// <summary>
        /// Searches for the player HUD canvas.
        /// Priority: local-player hierarchy → scene search by name → any overlay canvas.
        /// </summary>
        private static Canvas FindHUDCanvas()
        {
            // 1. Check the local player's own hierarchy.
            if ((object)Player.localPlayer != null)
            {
                foreach (string candidate in new[] { "HUD", "PlayerHUD", "Canvas_HUD", "UI" })
                {
                    Transform t = Player.localPlayer.transform.Find(candidate);
                    if ((object)t != null)
                    {
                        Canvas c = t.GetComponent<Canvas>();
                        if ((object)c != null) return c;
                    }
                }
            }

            // 2. Scene-wide search by name.
            Canvas[] all = Object.FindObjectsOfType<Canvas>();
            foreach (Canvas c in all)
            {
                string n = c.name.ToLowerInvariant();
                if (n.Contains("hud") || n.Contains("playerhud"))
                    return c;
            }

            // 3. Fall back to any screen-space overlay canvas.
            foreach (Canvas c in all)
            {
                if (c.renderMode == RenderMode.ScreenSpaceOverlay)
                    return c;
            }

            return null!; // caller checks with (object)canvas == null
        }
    }
}
