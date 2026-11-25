using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>
    /// The Stats HUD: the runtime readout of cell counts, step times, and time scale.
    /// M0 fills the grid, frame, and texture memory readouts. Later milestones fill the other readouts.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class StatsHud : MonoBehaviour
    {
        const float UpdateIntervalS = 0.25f;   // four updates per second
        const string NotBuilt = "--";

        FireCAApp _app;
        Label _fireGrid, _windGrid, _activeBlocks, _burnedArea, _fireSteps, _windSteps, _cellUpdates, _timeScale, _frame, _memory;
        float _elapsed;
        int _frames;

        /// <summary>The frame rate of the last update interval.</summary>
        public float FramesPerSecond { get; private set; }

        void Start()
        {
            _app = FireCAApp.Instance;
            var root = GetComponent<UIDocument>().rootVisualElement;

            var hud = new VisualElement { name = "stats-hud" };
            hud.pickingMode = PickingMode.Ignore;
            hud.style.position = Position.Absolute;
            hud.style.right = 10; hud.style.top = 10;
            hud.style.width = 290;
            hud.style.paddingLeft = 12; hud.style.paddingRight = 12;
            hud.style.paddingTop = 8; hud.style.paddingBottom = 8;
            hud.style.backgroundColor = UiStyle.PanelBackground;
            hud.style.borderTopLeftRadius = 4; hud.style.borderTopRightRadius = 4;
            hud.style.borderBottomLeftRadius = 4; hud.style.borderBottomRightRadius = 4;
            root.Add(hud);

            _fireGrid = AddRow(hud, "Fire Grid");
            _windGrid = AddRow(hud, "Wind Grid");
            _activeBlocks = AddRow(hud, "Active blocks");
            _burnedArea = AddRow(hud, "Burned area");
            _fireSteps = AddRow(hud, "Fire steps");
            _windSteps = AddRow(hud, "Wind steps");
            _cellUpdates = AddRow(hud, "Cell updates");
            _timeScale = AddRow(hud, "Time scale");
            _frame = AddRow(hud, "Frame");
            _memory = AddRow(hud, "Texture memory");
            Refresh();
        }

        static Label AddRow(VisualElement parent, string name)
        {
            var row = UiStyle.Row();
            row.pickingMode = PickingMode.Ignore;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginTop = 1;
            var label = UiStyle.Text_(name, 11, false, UiStyle.DimText);
            label.pickingMode = PickingMode.Ignore;
            row.Add(label);
            var value = UiStyle.Text_(NotBuilt, 11);
            value.pickingMode = PickingMode.Ignore;
            value.style.unityTextAlign = TextAnchor.MiddleRight;
            row.Add(value);
            parent.Add(row);
            return value;
        }

        void Update()
        {
            _elapsed += Time.unscaledDeltaTime;
            _frames++;
            if (_elapsed < UpdateIntervalS) return;
            FramesPerSecond = _frames / _elapsed;
            _elapsed = 0f;
            _frames = 0;
            Refresh();
        }

        void Refresh()
        {
            if (_frame == null) return;
            if (FramesPerSecond > 0f)
                _frame.text = $"{FramesPerSecond:F0} frames/s, {1000f / FramesPerSecond:F1} ms";

            if (_app == null || _app.Scenario == null)
            {
                _fireGrid.text = _windGrid.text = _memory.text = NotBuilt;
                return;
            }
            SpreadSolver spread = _app.Spread;
            StepScheduler scheduler = _app.Scheduler;
            if (spread != null && scheduler != null)
            {
                GridSpec grid = _app.Scenario.Grid;
                _activeBlocks.text = $"{spread.ActiveBlocks:N0} of {grid.WindCellsX * grid.WindCellsZ:N0}";
                _burnedArea.text = $"{spread.BurnedAreaHa:N1} ha";
                string gpu = scheduler.FireStepGpuMs > 0f ? $"{scheduler.FireStepGpuMs:F2} ms" : "no GPU timing";
                _fireSteps.text = $"{scheduler.FireStepsPerSecond:F0} steps/s, {gpu}";
                if (_app.Wind != null && _app.Wind.Enabled)
                {
                    string windGpu = scheduler.WindStepGpuMs > 0f ? $"{scheduler.WindStepGpuMs:F2} ms" : "no GPU timing";
                    _windSteps.text = scheduler.SpinningUp
                        ? $"spin-up {_app.Wind.SpinUpProgress * 100f:F0} percent"
                        : $"{scheduler.WindStepsPerSecond:F0} steps/s, {windGpu}";
                }
                else _windSteps.text = "off";
                _cellUpdates.text = $"{scheduler.CellUpdatesPerSecond / 1.0e9:F2} billion per second";
                SimClock clock = _app.Clock;
                _timeScale.text = clock.Playing
                    ? $"requested {clock.TimeScale:F0}x, achieved {clock.AchievedTimeScale:F0}x"
                    : $"requested {clock.TimeScale:F0}x, paused";
            }
            GridSpec g = _app.Scenario.Grid;
            _fireGrid.text = $"{g.FireCells} x {g.FireCells} = {g.FireCellTotal:N0} cells";
            _windGrid.text = $"{g.WindCellsX} x {GridSpec.WindCellsY} x {g.WindCellsZ} = {g.WindCellTotal:N0} cells";
            _memory.text = $"{_app.Registry.TotalBytes / (1024.0 * 1024.0):F0} MB";
        }
    }
}
