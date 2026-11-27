using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>
    /// Shared colors and element helpers of the Control Panel and the Stats HUD. The style sheet
    /// Assets/FireCA/UI/FireCA.uss holds the look of each class (docs/decisions.md, decision D35).
    /// </summary>
    static class UiStyle
    {
        public static readonly Color Text = Hex(0xE8F0F7);
        public static readonly Color DimText = Hex(0x9FB4C8);
        public static readonly Color Wind = Hex(0x5CC8EA);
        public static readonly Color Fire = Hex(0xF6A63A);
        public static readonly Color Line = Hex(0x2E4C6B);
        public static readonly Color Off = Hex(0x6F879E);

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

        /// <summary>Prepares the root of the UI document. The root does not block the pointer.</summary>
        public static void PrepareRoot(VisualElement root)
        {
            root.pickingMode = PickingMode.Ignore;
            root.style.flexGrow = 1;
            root.AddToClassList("fc-root");
        }

        /// <summary>A text element. The classes pick the font, the size, and the color.</summary>
        public static Label Text_(string text, params string[] classes)
        {
            var label = new Label(text);
            label.AddToClassList("fc-text");
            foreach (string c in classes) label.AddToClassList(c);
            return label;
        }

        public static VisualElement Box(params string[] classes)
        {
            var box = new VisualElement();
            foreach (string c in classes) box.AddToClassList(c);
            return box;
        }

        public static VisualElement Row(params string[] classes)
        {
            var row = Box(classes);
            row.AddToClassList("fc-row");
            return row;
        }

        /// <summary>The title of a group of controls.</summary>
        public static Label Caption(string title) => Text_(title.ToUpperInvariant(), "fc-caps");

        /// <summary>A row of the Stats HUD: a name on the left and a value on the right. Returns the value element.</summary>
        public static Label Readout(VisualElement parent, string name, bool fire = false)
        {
            var row = Box("fc-readout");
            row.Add(Text_(name, "fc-dim"));
            var value = Text_("--", "fc-mono");
            if (fire) value.AddToClassList("fc-fire");
            row.Add(value);
            parent.Add(row);
            return value;
        }

        /// <summary>The name of the compass point of a direction, from N to NNW.</summary>
        public static string CompassPoint(float degrees)
        {
            string[] points = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
            int index = Mathf.RoundToInt(Mathf.Repeat(degrees, 360f) / 22.5f) % 16;
            return points[index];
        }
    }
}
