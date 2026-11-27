using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    // The controls of the component catalog (docs/decisions.md, decision D35). Each control is a custom element,
    // so the UI uses no graphic of the default theme of Unity.

    /// <summary>A button with a text.</summary>
    sealed class UiButton : VisualElement
    {
        readonly Label _label;

        public UiButton(string text, Action onClick, bool primary = false)
        {
            AddToClassList("fc-button");
            if (primary) AddToClassList("fc-button--primary");
            _label = UiStyle.Text_(text);
            _label.pickingMode = PickingMode.Ignore;
            Add(_label);
            this.AddManipulator(new Clickable(onClick));
        }

        public string Text { get => _label.text; set => _label.text = value; }

        /// <summary>A row of buttons with equal widths.</summary>
        public static VisualElement RowOf(params UiButton[] buttons)
        {
            var row = UiStyle.Box("fc-row");
            for (int i = 0; i < buttons.Length; i++)
            {
                if (i > 0) row.Add(UiStyle.Box("fc-button-gap"));
                row.Add(buttons[i]);
            }
            return row;
        }
    }

    /// <summary>A tool toggle of the Tool Bar: an icon, an optional text, and an on state.</summary>
    sealed class UiToolToggle : VisualElement
    {
        readonly UiIcon _icon;
        readonly Color _onColor;
        readonly Action<bool> _onChange;

        public bool Value { get; private set; }

        public UiToolToggle(string iconPath, string text, bool fire, Action<bool> onChange)
        {
            _onChange = onChange;
            _onColor = fire ? UiStyle.Fire : UiStyle.Wind;
            AddToClassList("fc-tool");
            if (fire) AddToClassList("fc-tool--fire");
            _icon = new UiIcon(iconPath, 24f, UiStyle.DimText);
            Add(_icon);
            if (!string.IsNullOrEmpty(text))
            {
                AddToClassList("fc-tool--label");
                var label = UiStyle.Text_(text);
                label.pickingMode = PickingMode.Ignore;
                Add(label);
            }
            this.AddManipulator(new Clickable(() =>
            {
                SetValueWithoutNotify(!Value);
                _onChange?.Invoke(Value);
            }));
        }

        public void SetValueWithoutNotify(bool value)
        {
            if (value == Value) return;
            Value = value;
            EnableInClassList("fc-tool--on", value);
            _icon.SetColor(value ? _onColor : UiStyle.DimText);
        }
    }

    /// <summary>A switch with a text on the left.</summary>
    sealed class UiSwitch : VisualElement
    {
        readonly Action<bool> _onChange;

        public bool Value { get; private set; }

        public UiSwitch(string text, bool value, Action<bool> onChange, bool fire = false)
        {
            _onChange = onChange;
            AddToClassList("fc-switch");
            if (fire) AddToClassList("fc-switch--fire");
            var label = UiStyle.Text_(text);
            label.pickingMode = PickingMode.Ignore;
            Add(label);
            var track = UiStyle.Box("fc-switch-track");
            track.pickingMode = PickingMode.Ignore;
            var knob = UiStyle.Box("fc-switch-knob");
            knob.pickingMode = PickingMode.Ignore;
            track.Add(knob);
            Add(track);
            SetValueWithoutNotify(value);
            this.AddManipulator(new Clickable(() =>
            {
                SetValueWithoutNotify(!Value);
                _onChange?.Invoke(Value);
            }));
        }

        public void SetValueWithoutNotify(bool value)
        {
            Value = value;
            EnableInClassList("fc-switch--on", value);
        }
    }

    /// <summary>A slider with a name on the left and the value on the right.</summary>
    sealed class UiSlider : VisualElement
    {
        readonly float _low, _high, _step;
        readonly Func<float, string> _format;
        readonly Action<float> _onChange;
        readonly Label _name, _valueText;
        readonly VisualElement _area, _fill, _thumb;
        bool _dragging;

        public float Value { get; private set; }

        /// <param name="step">The slider snaps to a multiple of the step. 0 gives no snap.</param>
        public UiSlider(string name, float low, float high, float value, Func<float, string> format, Action<float> onChange, float step = 0f)
        {
            _low = low; _high = high; _step = step;
            _format = format ?? (v => v.ToString("F2"));
            _onChange = onChange;
            AddToClassList("fc-slider");

            var head = UiStyle.Box("fc-slider-head");
            _name = UiStyle.Text_(name, "fc-dim");
            head.Add(_name);
            _valueText = UiStyle.Text_("", "fc-mono", "fc-slider-value");
            head.Add(_valueText);
            Add(head);

            _area = UiStyle.Box("fc-slider-area");
            var rail = UiStyle.Box("fc-slider-rail");
            rail.pickingMode = PickingMode.Ignore;
            var track = UiStyle.Box("fc-slider-track");
            track.pickingMode = PickingMode.Ignore;
            _fill = UiStyle.Box("fc-slider-fill");
            _fill.pickingMode = PickingMode.Ignore;
            _thumb = UiStyle.Box("fc-slider-thumb");
            _thumb.pickingMode = PickingMode.Ignore;
            rail.Add(track);
            rail.Add(_fill);
            rail.Add(_thumb);
            _area.Add(rail);
            Add(_area);

            _area.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                _dragging = true;
                _area.CapturePointer(e.pointerId);
                Drag(e.localPosition.x);
                e.StopPropagation();
            });
            _area.RegisterCallback<PointerMoveEvent>(e => { if (_dragging) Drag(e.localPosition.x); });
            _area.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!_dragging) return;
                _dragging = false;
                _area.ReleasePointer(e.pointerId);
            });
            _area.RegisterCallback<PointerCaptureOutEvent>(e => _dragging = false);

            SetValueWithoutNotify(value);
        }

        public string Name { get => _name.text; set => _name.text = value; }

        void Drag(float localX)
        {
            float width = _area.resolvedStyle.width - 16f;
            if (width <= 1f) return;
            float value = Mathf.Lerp(_low, _high, Mathf.Clamp01((localX - 8f) / width));
            if (_step > 0f) value = Mathf.Clamp(Mathf.Round(value / _step) * _step, _low, _high);
            if (Mathf.Approximately(value, Value)) return;
            SetValueWithoutNotify(value);
            _onChange?.Invoke(Value);
        }

        public void SetValueWithoutNotify(float value)
        {
            // A drag owns the value until the pointer goes up.
            Value = Mathf.Clamp(value, _low, _high);
            float t = Mathf.Approximately(_high, _low) ? 0f : Mathf.InverseLerp(_low, _high, Value);
            _fill.style.right = Length.Percent((1f - t) * 100f);
            _thumb.style.left = Length.Percent(t * 100f);
            _valueText.text = _format(Value);
        }

        /// <summary>Sets the value from the model. The call does nothing while the user drags the slider.</summary>
        public void Sync(float value)
        {
            if (_dragging || Mathf.Approximately(value, Value)) return;
            SetValueWithoutNotify(value);
        }
    }

    /// <summary>A list that opens under its box. The open list is on top of every other element.</summary>
    sealed class UiSelect : VisualElement
    {
        readonly List<string> _choices;
        readonly Action<int> _onChange;
        readonly Label _label;
        VisualElement _blocker;

        public int Index { get; private set; }
        public string Value => Index >= 0 && Index < _choices.Count ? _choices[Index] : "";

        public UiSelect(List<string> choices, int index, Action<int> onChange)
        {
            _choices = choices;
            _onChange = onChange;
            AddToClassList("fc-select");
            _label = UiStyle.Text_("");
            _label.pickingMode = PickingMode.Ignore;
            Add(_label);
            Add(new UiIcon(UiIcon.ChevronDown, 18f, UiStyle.DimText));
            SetIndexWithoutNotify(index);
            this.AddManipulator(new Clickable(Open));
            RegisterCallback<DetachFromPanelEvent>(e => Close());
        }

        public void SetIndexWithoutNotify(int index)
        {
            Index = index;
            _label.text = Value;
        }

        public void SetValueWithoutNotify(string value)
        {
            int index = _choices.IndexOf(value);
            if (index >= 0 && index != Index) SetIndexWithoutNotify(index);
        }

        void Open()
        {
            if (_blocker != null || panel == null) return;
            VisualElement layer = panel.visualTree;
            // The blocker takes the next click outside the list and closes the list.
            _blocker = UiStyle.Box("fc-select-blocker", "fc-root");
            _blocker.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.target == _blocker) { Close(); e.StopPropagation(); }
            });
            var popup = UiStyle.Box("fc-select-popup");
            Rect box = layer.WorldToLocal(worldBound);
            popup.style.left = box.xMin;
            popup.style.top = box.yMax + 4f;
            popup.style.width = box.width;
            for (int i = 0; i < _choices.Count; i++)
            {
                int index = i;
                var option = UiStyle.Box("fc-select-option");
                if (i == Index) option.AddToClassList("fc-select-option--on");
                var text = UiStyle.Text_(_choices[i]);
                text.pickingMode = PickingMode.Ignore;
                option.Add(text);
                if (i == Index)
                {
                    var dot = UiStyle.Box("fc-select-dot");
                    dot.pickingMode = PickingMode.Ignore;
                    option.Add(dot);
                }
                option.AddManipulator(new Clickable(() =>
                {
                    Close();
                    if (index == Index) return;
                    SetIndexWithoutNotify(index);
                    _onChange?.Invoke(index);
                }));
                popup.Add(option);
            }
            _blocker.Add(popup);
            layer.Add(_blocker);
            // A list near the bottom edge of the screen opens above its box.
            popup.RegisterCallback<GeometryChangedEvent>(e =>
            {
                if (popup.worldBound.yMax > layer.worldBound.yMax - 8f)
                    popup.style.top = Mathf.Max(8f, box.yMin - 4f - popup.resolvedStyle.height);
            });
        }

        public void Close()
        {
            if (_blocker == null) return;
            _blocker.RemoveFromHierarchy();
            _blocker = null;
        }
    }

    /// <summary>A row of tabs over one content area.</summary>
    sealed class UiTabs : VisualElement
    {
        readonly VisualElement _head, _body;
        readonly List<VisualElement> _tabs = new List<VisualElement>();
        readonly List<UiIcon> _icons = new List<UiIcon>();
        readonly List<VisualElement> _pages = new List<VisualElement>();

        public int Selected { get; private set; } = -1;
        public event Action<int> Changed;

        public UiTabs()
        {
            _head = UiStyle.Box("fc-tabs-head");
            _body = new VisualElement();
            Add(_head);
            Add(_body);
        }

        /// <summary>Adds a tab and returns its page. The first tab is the selected tab.</summary>
        public VisualElement AddTab(string title, string iconPath = null, params string[] pageClasses)
        {
            int index = _tabs.Count;
            var tab = UiStyle.Box("fc-tab");
            UiIcon icon = null;
            if (iconPath != null)
            {
                icon = new UiIcon(iconPath, 18f, UiStyle.DimText);
                icon.AddToClassList("fc-tab-icon");
                tab.Add(icon);
            }
            var text = UiStyle.Text_(title.ToUpperInvariant());
            text.pickingMode = PickingMode.Ignore;
            tab.Add(text);
            tab.AddManipulator(new Clickable(() => Select(index)));
            _head.Add(tab);
            _tabs.Add(tab);
            _icons.Add(icon);

            var page = UiStyle.Box(pageClasses);
            _body.Add(page);
            _pages.Add(page);
            if (index == 0) Select(0);
            else page.style.display = DisplayStyle.None;
            return page;
        }

        public void Select(int index)
        {
            if (index == Selected || index < 0 || index >= _tabs.Count) return;
            Selected = index;
            for (int i = 0; i < _tabs.Count; i++)
            {
                bool on = i == index;
                _tabs[i].EnableInClassList("fc-tab--on", on);
                _icons[i]?.SetColor(on ? UiStyle.Wind : UiStyle.DimText);
                _pages[i].style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
            }
            Changed?.Invoke(index);
        }
    }
}
