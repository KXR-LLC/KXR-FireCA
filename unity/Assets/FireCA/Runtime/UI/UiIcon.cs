using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>
    /// An icon of the UI. An icon is one path on a grid of 24 x 24, drawn as a stroke with the vector painter of
    /// UI Toolkit, so the project holds no icon image. The paths are the paths of the component catalog
    /// (docs/decisions.md, decision D35). A path uses the commands M, L, C, and Z with absolute positions.
    /// </summary>
    sealed class UiIcon : VisualElement
    {
        public const string Gear = "M10.22 5.02 L10.47 2.52 L13.53 2.52 L13.78 5.02 L15.67 5.81 L17.62 4.22 L19.78 6.38 L18.19 8.33 L18.98 10.22 L21.48 10.47 L21.48 13.53 L18.98 13.78 L18.19 15.67 L19.78 17.62 L17.62 19.78 L15.67 18.19 L13.78 18.98 L13.53 21.48 L10.47 21.48 L10.22 18.98 L8.33 18.19 L6.38 19.78 L4.22 17.62 L5.81 15.67 L5.02 13.78 L2.52 13.53 L2.52 10.47 L5.02 10.22 L5.81 8.33 L4.22 6.38 L6.38 4.22 L8.33 5.81 Z M15.1 12 C15.1 13.71 13.71 15.1 12 15.1 C10.29 15.1 8.9 13.71 8.9 12 C8.9 10.29 10.29 8.9 12 8.9 C13.71 8.9 15.1 10.29 15.1 12 Z";
        public const string Flame = "M12 3 C12 7 17.5 9.5 17.5 14.5 C17.5 18 15 21 12 21 C9 21 6.5 18.5 6.5 15 C6.5 12.5 8 11 9 9.5 C9.5 11 10.2 11.8 11 12 C11.5 9 10.5 6 12 3 Z";
        public const string Play = "M8 5 L19 12 L8 19 Z";
        public const string Pause = "M9 5 L9 19 M15 5 L15 19";
        public const string Orbit = "M21.25 9.81 C20.73 11.32 18.95 13.06 16.41 14.52 C13.87 15.98 10.85 17.01 8.22 17.31 C5.59 17.61 3.63 17.16 2.9 16.07 C2.17 14.98 2.76 13.39 4.49 11.74 C6.22 10.09 8.91 8.56 11.79 7.6 C14.66 6.64 17.41 6.35 19.24 6.81 M16.33 3.79 L19.24 6.81 L15.25 8.1 M14.2 12 C14.2 13.22 13.22 14.2 12 14.2 C10.78 14.2 9.8 13.22 9.8 12 C9.8 10.78 10.78 9.8 12 9.8 C13.22 9.8 14.2 10.78 14.2 12 Z";
        public const string Close = "M6 6 L18 18 M18 6 L6 18";
        public const string Tools = "M18.5 12 C18.5 15.59 15.59 18.5 12 18.5 C8.41 18.5 5.5 15.59 5.5 12 C5.5 8.41 8.41 5.5 12 5.5 C15.59 5.5 18.5 8.41 18.5 12 Z M12 2.5 L12 7 M12 17 L12 21.5 M2.5 12 L7 12 M17 12 L21.5 12";
        public const string Weather = "M3 9 L14 9 C17.6 9 17.6 4.5 14.6 4.5 C13.4 4.5 12.6 5.2 12.3 6 M3 13 L20 13 M3 17 L12 17 C15.4 17 15.4 21.2 12.6 21.2 C11.5 21.2 10.8 20.6 10.5 19.8";
        public const string Display = "M12 4 L21 9 L12 14 L3 9 Z M3 14 L12 19 L21 14";
        public const string Minus = "M5 12 L19 12";
        public const string Plus = "M5 12 L19 12 M12 5 L12 19";
        public const string ChevronDown = "M6 9 L12 15 L18 9";

        struct Command { public char Op; public Vector2 A, B, C; }

        static readonly Dictionary<string, Command[]> Parsed = new Dictionary<string, Command[]>();

        string _path;
        Color _color;

        public UiIcon(string path, float sizePx, Color color)
        {
            _path = path;
            _color = color;
            pickingMode = PickingMode.Ignore;
            style.width = sizePx;
            style.height = sizePx;
            style.flexShrink = 0;
            generateVisualContent += Draw;
        }

        public void Set(string path, Color color)
        {
            if (path == _path && color == _color) return;
            _path = path;
            _color = color;
            MarkDirtyRepaint();
        }

        public void SetColor(Color color) => Set(_path, color);

        void Draw(MeshGenerationContext context)
        {
            float scale = contentRect.width / 24f;
            if (scale <= 0f) return;
            var painter = context.painter2D;
            painter.strokeColor = _color;
            painter.lineWidth = 1.75f * Mathf.Max(scale, 0.8f);
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;
            painter.BeginPath();
            foreach (Command c in Parse(_path))
            {
                switch (c.Op)
                {
                    case 'M': painter.MoveTo(c.A * scale); break;
                    case 'L': painter.LineTo(c.A * scale); break;
                    case 'C': painter.BezierCurveTo(c.A * scale, c.B * scale, c.C * scale); break;
                    case 'Z': painter.ClosePath(); break;
                }
            }
            painter.Stroke();
        }

        static Command[] Parse(string path)
        {
            if (Parsed.TryGetValue(path, out Command[] found)) return found;
            var commands = new List<Command>();
            string[] tokens = path.Replace("M", " M ").Replace("L", " L ").Replace("C", " C ").Replace("Z", " Z ")
                .Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            int at = 0;
            Vector2 Next()
            {
                float x = float.Parse(tokens[at++], CultureInfo.InvariantCulture);
                float y = float.Parse(tokens[at++], CultureInfo.InvariantCulture);
                return new Vector2(x, y);
            }
            while (at < tokens.Length)
            {
                var command = new Command { Op = tokens[at++][0] };
                if (command.Op == 'M' || command.Op == 'L') command.A = Next();
                else if (command.Op == 'C') { command.A = Next(); command.B = Next(); command.C = Next(); }
                commands.Add(command);
            }
            found = commands.ToArray();
            Parsed[path] = found;
            return found;
        }
    }

    /// <summary>
    /// The wind dial of the Stats HUD. North is up. The arrow points the way the air moves, so a wind from the
    /// west has an arrow that points east.
    /// </summary>
    sealed class WindDial : VisualElement
    {
        float _fromDeg;
        bool _calm;

        public WindDial(float sizePx)
        {
            pickingMode = PickingMode.Ignore;
            style.width = sizePx;
            style.height = sizePx;
            style.flexShrink = 0;
            generateVisualContent += Draw;
        }

        /// <summary>Sets the direction that the wind comes from, in degrees from north through east.</summary>
        public void Set(float fromDeg, bool calm)
        {
            if (Mathf.Abs(fromDeg - _fromDeg) < 0.25f && calm == _calm) return;
            _fromDeg = fromDeg;
            _calm = calm;
            MarkDirtyRepaint();
        }

        void Draw(MeshGenerationContext context)
        {
            float size = contentRect.width;
            if (size <= 0f) return;
            var painter = context.painter2D;
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            float radius = size * 0.5f - 6f;
            painter.lineCap = LineCap.Round;
            painter.lineJoin = LineJoin.Round;

            painter.strokeColor = UiStyle.Line;
            painter.lineWidth = 1.5f;
            painter.BeginPath();
            painter.Arc(center, radius, 0f, 360f);
            painter.Stroke();

            // The four ticks. The north tick is longer.
            painter.strokeColor = UiStyle.DimText;
            painter.BeginPath();
            for (int k = 0; k < 4; k++)
            {
                Vector2 dir = Direction(k * 90f);
                painter.MoveTo(center + dir * radius);
                painter.LineTo(center + dir * (radius - (k == 0 ? 9f : 5f)));
            }
            painter.Stroke();

            if (_calm) return;
            Vector2 flow = Direction(_fromDeg + 180f);
            Vector2 side = new Vector2(-flow.y, flow.x);
            float reach = radius - 13f;
            Vector2 tip = center + flow * reach;
            painter.strokeColor = UiStyle.Wind;
            painter.lineWidth = 2.5f;
            painter.BeginPath();
            painter.MoveTo(center - flow * reach);
            painter.LineTo(tip);
            painter.MoveTo(tip - flow * 9f + side * 7f);
            painter.LineTo(tip);
            painter.LineTo(tip - flow * 9f - side * 7f);
            painter.Stroke();
        }

        // The screen direction of a compass direction. North is up, and east is right.
        static Vector2 Direction(float degrees)
        {
            float a = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(a), -Mathf.Cos(a));
        }
    }
}
