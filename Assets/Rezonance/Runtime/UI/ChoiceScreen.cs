// REZONANCE VN ENGINE - choice menu (UI module).
// Modal option list on ChoiceLayer. It polls the keyboard itself (arrows + Enter + 1..9) because the
// engine input map is owned by another module; uGUI navigation events are switched off while open so
// the focus cannot move twice per frame.
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UI;
using TMPro;
using Rezonance.VN.Core;

namespace Rezonance.VN.UI
{
    /// <summary>Numbered option list for a VNMenu node.</summary>
    [DisallowMultipleComponent]
    public sealed class ChoiceScreen : MonoBehaviour
    {
        private const string Area = "choices";

        /// <summary>Router/screen id.</summary>
        public const string Id = "choices";

        private readonly List<Button> cells = new List<Button>();
        private readonly List<Image> cellImages = new List<Image>();
        private readonly List<TMP_Text> cellLabels = new List<TMP_Text>();
        private readonly List<TextMeshProUGUI> cellNumbers = new List<TextMeshProUGUI>();
        private readonly List<bool> enabledFlags = new List<bool>();

        private RectTransform root;
        private RectTransform list;
        private TextMeshProUGUI promptLabel;
        private Action<int> chooser;
        private VNNode menu;
        private int focusIndex = -1;
        private bool sendNavigation = true;

        /// <summary>True while the list is on screen.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>Number of visible options.</summary>
        public int Count => enabledFlags.Count;

        /// <summary>Currently highlighted option, -1 when nothing is focused.</summary>
        public int FocusIndex => focusIndex;

        /// <summary>Menu node the list was built for.</summary>
        public VNNode Menu => menu;

        // ---------------------------------------------------------------- construction

        /// <summary>Mounts the choice list on ChoiceLayer (or a custom parent).</summary>
        public static ChoiceScreen Mount(Transform parent = null)
        {
            var layer = parent != null ? parent : UIBuilder.GetLayer("ChoiceLayer");
            var go = new GameObject("ChoiceScreen", typeof(RectTransform));
            var screen = go.AddComponent<ChoiceScreen>();
            UIBuilder.Safe("ChoiceScreen.Build", () => screen.Build(layer));
            return screen;
        }

        private void Build(Transform layer)
        {
            root = transform as RectTransform;
            root.SetParent(layer, false);
            UIBuilder.Stretch(root);

            promptLabel = UIBuilder.CreateText(root, "MenuPrompt", UiStyle.HeadingFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(promptLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              new Vector2(0.5f, 1f), new Vector2(0f, 60f), new Vector2(1400f, 120f));
            promptLabel.alignment = TextAlignmentOptions.Bottom;
            promptLabel.raycastTarget = false;

            list = UIBuilder.CreateRect("Options", root);
            UIBuilder.SetRect(list, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                              new Vector2(0f, -120f), new Vector2(1300f, 0f));
            var vlg = UIBuilder.SetLayoutVertical(list, 12f, 0f, true);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = false;

            root.gameObject.SetActive(false);
        }

        private Button EnsureCell(int index)
        {
            if (index < cells.Count) return cells[index];

            var buttonRt = UIBuilder.CreateNineSlicePanel("Option_" + (index + 1), list, UiStyle.PanelRaised);
            var le = buttonRt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = Mathf.Max(UIBuilder.MinTouchSize, UiStyle.ButtonHeight);
            le.preferredWidth = 1240f;
            le.flexibleWidth = 0f;

            var number = UIBuilder.CreateText(buttonRt.transform, "Number", UiStyle.LabelFontSize, UiStyle.AccentWarm, false);
            UIBuilder.SetRect(number.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                              Vector2.zero, new Vector2(64f, 0f));
            number.alignment = TextAlignmentOptions.Midline;
            number.raycastTarget = false;

            var label = UIBuilder.CreateText(buttonRt.transform, "Label", UiStyle.LabelFontSize, UiStyle.Text, true);
            UIBuilder.SetRect(label.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                              new Vector2(32f, 0f), new Vector2(-72f, 0f));
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.overflowMode = TextOverflowModes.Shrink;
            label.margin = new Vector4(8f, 6f, 16f, 6f);
            label.raycastTarget = false;

            var button = buttonRt.gameObject.AddComponent<Button>();
            button.targetGraphic = buttonRt;
            button.colors = UiStyle.ButtonColors(UiStyle.PanelRaised);
            var captured = index;
            button.onClick.AddListener(() => UIBuilder.Safe("choice." + captured, () => Choose(captured)));

            cells.Add(button);
            cellImages.Add(buttonRt);
            cellLabels.Add(label);
            cellNumbers.Add(number);
            return button;
        }

        // ---------------------------------------------------------------- public api

        /// <summary>Callback the orchestrator supplies: it must call IVNRuntime.Choose(index).</summary>
        public void SetChooser(Action<int> onChoose)
        {
            chooser = onChoose;
        }

        /// <summary>Builds the list. Null labels fall back to the node options, null flags mean "all enabled".</summary>
        public void Show(VNNode menuNode, IReadOnlyList<string> localizedLabels, IReadOnlyList<bool> enabled)
        {
            if (root == null) Build(UIBuilder.GetLayer("ChoiceLayer"));
            menu = menuNode;

            var labels = localizedLabels;
            if ((labels == null || labels.Count == 0) && menuNode != null && menuNode.options != null)
            {
                var fallback = new List<string>(menuNode.options.Count);
                for (var i = 0; i < menuNode.options.Count; i++)
                    fallback.Add(menuNode.options[i] != null ? menuNode.options[i].label : string.Empty);
                labels = fallback;
            }
            var count = labels != null ? labels.Count : 0;

            enabledFlags.Clear();
            for (var i = 0; i < count; i++)
            {
                var cell = EnsureCell(i);
                cell.gameObject.SetActive(true);
                cellLabels[i].text = labels[i] ?? string.Empty;
                cellNumbers[i].text = (i + 1).ToString();
                cell.interactable = enabled == null || i >= enabled.Count || enabled[i];
                enabledFlags.Add(cell.interactable);
            }
            for (var i = count; i < cells.Count; i++) cells[i].gameObject.SetActive(false);

            var prompt = menuNode != null && !string.IsNullOrEmpty(menuNode.menuPrompt) ? menuNode.menuPrompt : string.Empty;
            promptLabel.text = prompt;
            promptLabel.gameObject.SetActive(!string.IsNullOrEmpty(prompt));

            SetFocus(FirstEnabled());
            root.gameObject.SetActive(true);
            IsOpen = true;
            if (EventSystem.current != null)
            {
                sendNavigation = EventSystem.current.sendNavigationEvents;
                EventSystem.current.sendNavigationEvents = false;
            }
            UIBuilder.Rebuild(list);
        }

        /// <summary>Hides the list and returns navigation control to the rest of the UI.</summary>
        public void Hide()
        {
            IsOpen = false;
            menu = null;
            focusIndex = -1;
            if (root != null) root.gameObject.SetActive(false);
            if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = sendNavigation;
        }

        /// <summary>Picks an option: hides the list and forwards to the runtime callback.</summary>
        public void Choose(int index)
        {
            if (index < 0 || index >= enabledFlags.Count)
            {
                VNLog.Warn(Area, "Choice index out of range: " + index);
                return;
            }
            if (!enabledFlags[index])
            {
                VNLog.Info(Area, "Option " + index + " is disabled");
                return;
            }
            Hide();
            var handler = chooser;
            if (handler == null)
            {
                VNLog.Warn(Area, "No chooser callback bound - the menu cannot be resolved");
                return;
            }
            handler(index);
        }

        /// <summary>Moves the highlight by delta, skipping disabled rows.</summary>
        public void MoveFocus(int delta)
        {
            if (enabledFlags.Count == 0) return;
            var next = focusIndex < 0 ? (delta >= 0 ? 0 : enabledFlags.Count - 1) : focusIndex;
            for (var step = 0; step < enabledFlags.Count; step++)
            {
                next = (next + delta + enabledFlags.Count) % enabledFlags.Count;
                if (next >= 0 && next < enabledFlags.Count && enabledFlags[next]) break;
            }
            SetFocus(next);
        }

        /// <summary>Highlights one row.</summary>
        public void SetFocus(int index)
        {
            focusIndex = index;
            for (var i = 0; i < cellImages.Count; i++)
            {
                if (cellImages[i] == null) continue;
                var on = i == index;
                cellImages[i].color = on ? UiStyle.PanelActive : UiStyle.PanelRaised;
                if (cellLabels[i] != null) cellLabels[i].color = on ? UiStyle.AccentWarm : UiStyle.Text;
                if (cellNumbers[i] != null) cellNumbers[i].color = on ? UiStyle.AccentWarm : UiStyle.TextDim;
            }
            if (index >= 0 && index < cells.Count && cells[index] != null && EventSystem.current != null)
            {
                try { EventSystem.current.SetSelectedGameObject(cells[index].gameObject); }
                catch (Exception e) { VNLog.Warn(Area, "Focus set failed: " + e.Message); }
            }
        }

        /// <summary>Activates the highlighted row (Enter/Space).</summary>
        public void ActivateFocused()
        {
            if (focusIndex >= 0) Choose(focusIndex);
        }

        private int FirstEnabled()
        {
            for (var i = 0; i < enabledFlags.Count; i++) if (enabledFlags[i]) return i;
            return enabledFlags.Count > 0 ? 0 : -1;
        }

        // ---------------------------------------------------------------- keyboard

        private void Update()
        {
            if (!IsOpen || !gameObject.activeInHierarchy) return;
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame)
                UIBuilder.Safe("choices.down", () => MoveFocus(1));
            else if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame)
                UIBuilder.Safe("choices.up", () => MoveFocus(-1));
            else if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame
                     || keyboard.spaceKey.wasPressedThisFrame)
                UIBuilder.Safe("choices.enter", ActivateFocused);

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                // Escape belongs to the router (pause menu); do nothing here.
                return;
            }

            for (var d = 0; d < 9; d++)
            {
                var control = DigitalKeyFor(d);
                if (control == null || !control.wasPressedThisFrame) continue;
                var index = d;
                UIBuilder.Safe("choices.digit", () => Choose(index));
                break;
            }
        }

        private KeyControl DigitalKeyFor(int digitIndex)
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return null;
            switch (digitIndex)
            {
                case 0: return keyboard.digit1Key;
                case 1: return keyboard.digit2Key;
                case 2: return keyboard.digit3Key;
                case 3: return keyboard.digit4Key;
                case 4: return keyboard.digit5Key;
                case 5: return keyboard.digit6Key;
                case 6: return keyboard.digit7Key;
                case 7: return keyboard.digit8Key;
                case 8: return keyboard.digit9Key;
                default: return null;
            }
        }

        private void OnDestroy()
        {
            chooser = null;
            cells.Clear();
            cellImages.Clear();
            cellLabels.Clear();
            cellNumbers.Clear();
            enabledFlags.Clear();
            if (EventSystem.current != null) EventSystem.current.sendNavigationEvents = sendNavigation;
        }
    }
}
