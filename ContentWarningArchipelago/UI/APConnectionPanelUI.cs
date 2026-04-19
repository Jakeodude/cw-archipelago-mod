// UI/APConnectionPanelUI.cs
// In-game Archipelago connection panel, injected into the Mod Manager menu via
// Patches/ModManagerAPPatch.cs.
//
// UI is built entirely in code (no Unity Editor / prefab required), following
// the same programmatic pattern used by APNotificationUI.cs.
//
// Layout (vertical stack, dark semi-transparent background):
//
//   ── Archipelago Connection ──
//   Address:Port   [archipelago.gg:38281        ]
//   Slot Name      [                            ]
//   Password       [••••••••                    ]
//   [Connect]  [Disconnect]    ● Disconnected
//
// Multiplayer safety:
//   • This GameObject is spawned locally — it is NEVER serialised or sent over Photon.
//   • ConfigEntry.Value writes only to the local BepInEx .cfg file.
//   • Plugin.Connect() initiates a TCP connection on the calling client only.

using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ContentWarningArchipelago.UI
{
    /// <summary>
    /// MonoBehaviour panel that provides in-game Archipelago connection controls.
    /// Attached to a new GameObject injected into <see cref="ModManagerUI"/> by
    /// <c>ModManagerAPPatch</c>.
    /// </summary>
    public class APConnectionPanelUI : MonoBehaviour
    {
        // ------------------------------------------------------------------
        // UI element references (null-forgiving init — Unity lifetime managed)
        // ------------------------------------------------------------------

        private TMP_InputField _addressField  = null!;
        private TMP_InputField _slotField     = null!;
        private TMP_InputField _passwordField = null!;
        private Button         _connectButton    = null!;
        private Button         _disconnectButton = null!;
        private TextMeshProUGUI _statusLabel     = null!;

        // Colour constants for status label
        private static readonly Color ColDisconnected = new Color(0.90f, 0.22f, 0.22f);
        private static readonly Color ColConnecting   = new Color(1.00f, 0.85f, 0.10f);
        private static readonly Color ColConnected    = new Color(0.20f, 0.88f, 0.20f);

        // ================================================================== Unity lifecycle

        private void Awake()
        {
            BuildUI();
            PopulateFromConfig();
        }

        private void Update()
        {
            // Unity-safe null guard
            if ((object)_statusLabel == null) return;

            if (Plugin.isConnecting)
            {
                _statusLabel.text  = "⟳  Connecting\u2026";
                _statusLabel.color = ColConnecting;
            }
            else if (Plugin.connection != null && Plugin.connection.connected)
            {
                _statusLabel.text  = "●  Connected!";
                _statusLabel.color = ColConnected;
            }
            else
            {
                _statusLabel.text  = "●  Disconnected";
                _statusLabel.color = ColDisconnected;
            }
        }

        // ================================================================== Config sync

        /// <summary>Reads the current <see cref="APConfig"/> values into the UI fields.</summary>
        public void PopulateFromConfig()
        {
            if (Plugin.APConfig == null) return;

            if ((object)_addressField  != null)
                _addressField.text  = $"{Plugin.APConfig.address.Value}:{Plugin.APConfig.port.Value}";
            if ((object)_slotField     != null)
                _slotField.text     = Plugin.APConfig.slot.Value;
            if ((object)_passwordField != null)
                _passwordField.text = Plugin.APConfig.password.Value;
        }

        // ------------------------------------------------------------------ field change handlers

        private void OnAddressChanged(string value)
        {
            if (Plugin.APConfig == null) return;

            // Accept "host:port" or plain "host"
            int colonIdx = value.LastIndexOf(':');
            if (colonIdx > 0 && colonIdx < value.Length - 1)
            {
                string host    = value.Substring(0, colonIdx);
                string portStr = value.Substring(colonIdx + 1);
                Plugin.APConfig.address.Value = host;
                if (int.TryParse(portStr, out int parsedPort) && parsedPort > 0 && parsedPort <= 65535)
                    Plugin.APConfig.port.Value = parsedPort;
            }
            else if (colonIdx < 0)
            {
                Plugin.APConfig.address.Value = value.Trim();
            }

            Plugin.Logger.LogDebug(
                $"[APPanel] Address updated: {Plugin.APConfig.address.Value}:{Plugin.APConfig.port.Value}");
        }

        private void OnSlotChanged(string value)
        {
            if (Plugin.APConfig == null) return;
            Plugin.APConfig.slot.Value = value;
            Plugin.Logger.LogDebug($"[APPanel] Slot updated: {value}");
        }

        private void OnPasswordChanged(string value)
        {
            if (Plugin.APConfig == null) return;
            Plugin.APConfig.password.Value = value;
            Plugin.Logger.LogDebug("[APPanel] Password updated.");
        }

        // ------------------------------------------------------------------ button handlers

        private void OnConnectClicked()
        {
            if (Plugin.isConnecting || (Plugin.connection?.connected ?? false)) return;
            Plugin.Logger.LogInfo("[APPanel] Connect button clicked.");
            Plugin.Connect();
        }

        private void OnDisconnectClicked()
        {
            Plugin.Logger.LogInfo("[APPanel] Disconnect button clicked.");
            Plugin.Disconnect();
        }

        // ================================================================== Programmatic UI builder

        private void BuildUI()
        {
            // ---- Root panel: vertical stack, dark background ----
            var rootRect = gameObject.GetComponent<RectTransform>()
                           ?? gameObject.AddComponent<RectTransform>();

            // Let parent layout control width; size to preferred height.
            var csf = gameObject.AddComponent<ContentSizeFitter>();
            csf.verticalFit   = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            var vlg = gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding              = new RectOffset(10, 10, 8, 8);
            vlg.spacing              = 5f;
            vlg.childAlignment       = TextAnchor.UpperLeft;
            vlg.childControlWidth    = true;
            vlg.childControlHeight   = false;
            vlg.childForceExpandWidth  = true;
            vlg.childForceExpandHeight = false;

            // Semi-transparent dark card
            var bg = gameObject.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.05f, 0.72f);

            // ---- Header ----
            CreateLabel(
                transform,
                "── Archipelago Connection ──",
                fontSize:  16f,
                style:     FontStyles.Bold,
                alignment: TextAlignmentOptions.Center,
                height:    26f,
                color:     new Color(1f, 0.92f, 0.016f));

            // ---- Input rows ----
            _addressField  = CreateLabeledField(transform, "Address : Port",
                $"{Plugin.apAddress}:{Plugin.apPort}",
                isPassword: false);

            _slotField     = CreateLabeledField(transform, "Slot Name",
                Plugin.apSlot,
                isPassword: false);

            _passwordField = CreateLabeledField(transform, "Password",
                Plugin.apPassword,
                isPassword: true);

            // Wire up end-edit listeners (fires when user presses Enter or clicks away)
            _addressField.onEndEdit.AddListener(OnAddressChanged);
            _slotField.onEndEdit.AddListener(OnSlotChanged);
            _passwordField.onEndEdit.AddListener(OnPasswordChanged);

            // ---- Button + status row ----
            CreateButtonRow(transform);
        }

        // ================================================================== Row helpers

        /// <summary>
        /// Creates a horizontal row containing a label on the left and a
        /// <see cref="TMP_InputField"/> on the right, then returns the input field.
        /// </summary>
        private static TMP_InputField CreateLabeledField(
            Transform parent, string labelText, string initialValue, bool isPassword)
        {
            // Row container
            var row     = new GameObject($"APRow_{labelText}");
            row.transform.SetParent(parent, false);

            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 28f;

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing              = 6f;
            hlg.childAlignment       = TextAnchor.MiddleLeft;
            hlg.childControlWidth    = false;
            hlg.childControlHeight   = true;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = false;

            // Label
            var lblGO = new GameObject("Label");
            lblGO.transform.SetParent(row.transform, false);
            var lblLE  = lblGO.AddComponent<LayoutElement>();
            lblLE.preferredWidth  = 110f;
            lblLE.preferredHeight = 28f;

            var lblTmp = lblGO.AddComponent<TextMeshProUGUI>();
            lblTmp.text      = labelText;
            lblTmp.fontSize  = 13f;
            lblTmp.alignment = TextAlignmentOptions.MidlineRight;
            lblTmp.color     = new Color(0.85f, 0.85f, 0.85f);

            // Input field
            var field    = CreateInputField(row.transform, placeholder: labelText, isPassword);
            var fieldLE  = field.gameObject.GetComponent<LayoutElement>()
                           ?? field.gameObject.AddComponent<LayoutElement>();
            fieldLE.flexibleWidth   = 1f;
            fieldLE.preferredHeight = 28f;

            field.text = initialValue;
            return field;
        }

        /// <summary>Creates the [Connect] [Disconnect] row with the status label.</summary>
        private void CreateButtonRow(Transform parent)
        {
            var row = new GameObject("APButtonRow");
            row.transform.SetParent(parent, false);

            var rowLE = row.AddComponent<LayoutElement>();
            rowLE.preferredHeight = 32f;

            var hlg = row.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing              = 8f;
            hlg.childAlignment       = TextAnchor.MiddleLeft;
            hlg.childControlWidth    = false;
            hlg.childControlHeight   = true;
            hlg.childForceExpandWidth  = false;
            hlg.childForceExpandHeight = false;

            _connectButton    = CreateButton(row.transform, "Connect",    new Color(0.16f, 0.56f, 0.16f), 88f);
            _disconnectButton = CreateButton(row.transform, "Disconnect", new Color(0.50f, 0.13f, 0.13f), 100f);

            _connectButton.onClick.AddListener(OnConnectClicked);
            _disconnectButton.onClick.AddListener(OnDisconnectClicked);

            // Status label — fills remaining space
            var statusGO = new GameObject("StatusLabel");
            statusGO.transform.SetParent(row.transform, false);

            var statusLE = statusGO.AddComponent<LayoutElement>();
            statusLE.flexibleWidth   = 1f;
            statusLE.preferredHeight = 32f;

            _statusLabel           = statusGO.AddComponent<TextMeshProUGUI>();
            _statusLabel.fontSize  = 13f;
            _statusLabel.alignment = TextAlignmentOptions.MidlineLeft;
            _statusLabel.text      = "●  Disconnected";
            _statusLabel.color     = ColDisconnected;
        }

        // ================================================================== Primitive builders

        /// <summary>Creates a standalone <see cref="TextMeshProUGUI"/> label.</summary>
        private static TextMeshProUGUI CreateLabel(
            Transform parent,
            string text,
            float fontSize,
            FontStyles style,
            TextAlignmentOptions alignment,
            float height,
            Color color = default)
        {
            var go = new GameObject("APLabel");
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;

            var tmp       = go.AddComponent<TextMeshProUGUI>();
            tmp.text      = text;
            tmp.fontSize  = fontSize;
            tmp.fontStyle = style;
            tmp.alignment = alignment;
            tmp.color     = (color == default) ? Color.white : color;

            return tmp;
        }

        /// <summary>
        /// Builds a properly wired <see cref="TMP_InputField"/> with a placeholder,
        /// viewport mask, and optional password masking.
        /// </summary>
        private static TMP_InputField CreateInputField(
            Transform parent, string placeholder, bool isPassword)
        {
            // ---- Root ----
            var root = new GameObject("InputField");
            root.transform.SetParent(parent, false);

            var rootRect  = root.AddComponent<RectTransform>();
            var rootImage = root.AddComponent<Image>();
            rootImage.color = new Color(0.12f, 0.12f, 0.12f, 0.95f);

            var inputField = root.AddComponent<TMP_InputField>();

            // ---- Text Area (viewport with mask) ----
            var area     = new GameObject("Text Area");
            area.transform.SetParent(root.transform, false);
            var areaRect = area.AddComponent<RectTransform>();
            areaRect.anchorMin = Vector2.zero;
            areaRect.anchorMax = Vector2.one;
            areaRect.offsetMin = new Vector2(5f,  2f);
            areaRect.offsetMax = new Vector2(-5f, -2f);
            area.AddComponent<RectMask2D>();

            // ---- Placeholder ----
            var phGO   = new GameObject("Placeholder");
            phGO.transform.SetParent(area.transform, false);
            var phRect = phGO.AddComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = Vector2.zero;
            phRect.offsetMax = Vector2.zero;
            var phTmp       = phGO.AddComponent<TextMeshProUGUI>();
            phTmp.text      = placeholder;
            phTmp.fontSize  = 13f;
            phTmp.color     = new Color(0.65f, 0.65f, 0.65f, 0.65f);
            phTmp.alignment = TextAlignmentOptions.MidlineLeft;
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.raycastTarget = false;

            // ---- Text ----
            var txtGO   = new GameObject("Text");
            txtGO.transform.SetParent(area.transform, false);
            var txtRect = txtGO.AddComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;
            var txtTmp       = txtGO.AddComponent<TextMeshProUGUI>();
            txtTmp.fontSize  = 13f;
            txtTmp.color     = Color.white;
            txtTmp.alignment = TextAlignmentOptions.MidlineLeft;
            txtTmp.raycastTarget = false;

            // ---- Wire TMP_InputField references ----
            inputField.textViewport   = areaRect;
            inputField.textComponent  = txtTmp;
            inputField.placeholder    = phTmp;

            if (isPassword)
            {
                inputField.contentType = TMP_InputField.ContentType.Password;
                inputField.inputType   = TMP_InputField.InputType.Password;
            }
            else
            {
                inputField.contentType = TMP_InputField.ContentType.Standard;
            }

            return inputField;
        }

        /// <summary>Creates a coloured <see cref="Button"/> with a text label.</summary>
        private static Button CreateButton(Transform parent, string label, Color bgColor, float width)
        {
            var go = new GameObject($"Btn_{label}");
            go.transform.SetParent(parent, false);

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth  = width;
            le.preferredHeight = 28f;

            var img   = go.AddComponent<Image>();
            img.color = bgColor;

            var btn = go.AddComponent<Button>();

            // Colour block for hover/click feedback
            var cb           = btn.colors;
            cb.normalColor   = bgColor;
            cb.highlightedColor = bgColor * 1.25f;
            cb.pressedColor  = bgColor * 0.75f;
            cb.disabledColor = new Color(0.35f, 0.35f, 0.35f);
            btn.colors       = cb;
            btn.targetGraphic = img;

            // Label text
            var txtGO = new GameObject("Text");
            txtGO.transform.SetParent(go.transform, false);
            var txtRect = txtGO.AddComponent<RectTransform>();
            txtRect.anchorMin = Vector2.zero;
            txtRect.anchorMax = Vector2.one;
            txtRect.offsetMin = Vector2.zero;
            txtRect.offsetMax = Vector2.zero;
            var tmp       = txtGO.AddComponent<TextMeshProUGUI>();
            tmp.text      = label;
            tmp.fontSize  = 13f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color     = Color.white;
            tmp.raycastTarget = false;

            return btn;
        }
    }
}
