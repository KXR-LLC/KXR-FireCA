using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>Shared colors and element helpers of the Control Panel and the Stats HUD.</summary>
    static class UiStyle
    {
        public static readonly Color PanelBackground = new Color(0.07f, 0.08f, 0.10f, 0.90f);
        public static readonly Color Text = new Color(0.90f, 0.91f, 0.93f);
        public static readonly Color DimText = new Color(0.62f, 0.65f, 0.70f);
        public static readonly Color Accent = new Color(1.00f, 0.62f, 0.20f);
        public static readonly Color Error = new Color(1.00f, 0.42f, 0.36f);
        public static readonly Color Line = new Color(1f, 1f, 1f, 0.10f);

        public static Label Text_(string text, int size = 12, bool bold = false, Color? color = null)
        {
            var label = new Label(text);
            label.style.fontSize = size;
            label.style.color = color ?? Text;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
            label.style.marginLeft = 0; label.style.marginRight = 0;
            label.style.paddingLeft = 0; label.style.paddingRight = 0;
            return label;
        }

        public static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        public static Button Button(string text, System.Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.style.flexGrow = 1;
            button.style.height = 24;
            button.style.fontSize = 12;
            return button;
        }

        public static VisualElement Section(string title)
        {
            var section = new VisualElement();
            section.style.marginTop = 10;
            section.style.paddingTop = 8;
            section.style.borderTopWidth = 1;
            section.style.borderTopColor = Line;
            var header = Text_(title.ToUpperInvariant(), 11, true, Accent);
            header.style.marginBottom = 6;
            header.style.letterSpacing = 1;
            section.Add(header);
            return section;
        }
    }
}
