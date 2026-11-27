using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>
    /// The Stats HUD: the runtime readout of the run (docs/decisions.md, decision D35). The Conditions tab shows
    /// the Scenario, the wind, the Sim Clock, the weather, and the fire areas. The Performance tab shows cell
    /// counts, step times, and the frame rate.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class StatsHud : MonoBehaviour
    {
        const float UpdateIntervalS = 0.25f;   // four updates per second
        const string NotBuilt = "--";

        FireCAApp _app;
        Vegetation _vegetation;
        UiTabs _tabs;
        // Conditions
        Label _scenario, _terrain, _windSpeed, _windFrom, _clock, _elapsed, _conditionsTimeScale;
        Label _air, _humidity, _sun, _deadMoisture, _liveMoisture, _burning, _burnt, _burningPlants;
        WindDial _dial;
        // Performance
        Label _plants, _windNest, _fireGrid, _windGrid, _activeBlocks, _burnedArea, _fireSteps, _windSteps, _cellUpdates, _timeScale, _frame, _memory;
        float _elapsedS;
        int _frames;

        /// <summary>The frame rate of the last update interval.</summary>
        public float FramesPerSecond { get; private set; }
        /// <summary>The names of the two tabs.</summary>
        public static readonly string[] TabNames = { "Conditions", "Performance" };
        /// <summary>The index of the tab on show.</summary>
        public int SelectedTab => _tabs != null ? _tabs.Selected : 0;
        public void SelectTab(int index) => _tabs?.Select(index);

        void Start()
        {
            _app = FireCAApp.Instance;
            var root = GetComponent<UIDocument>().rootVisualElement;
            UiStyle.PrepareRoot(root);

            var hud = UiStyle.Box("fc-card", "fc-hud");
            hud.name = "stats-hud";
            root.Add(hud);
            _tabs = new UiTabs { name = "stats-tabs" };
            hud.Add(_tabs);

            BuildConditions(_tabs.AddTab(TabNames[0], null, "fc-hud-body"));
            BuildPerformance(_tabs.AddTab(TabNames[1], null, "fc-hud-body"));
            Refresh();
        }

        void BuildConditions(VisualElement page)
        {
            page.Add(UiStyle.Caption("Scenario"));
            _scenario = UiStyle.Text_(NotBuilt, "fc-bold", "fc-scenario-name");
            page.Add(_scenario);
            _terrain = UiStyle.Text_("", "fc-mono", "fc-small", "fc-dim");
            page.Add(_terrain);

            var wind = UiStyle.Row();
            wind.style.marginTop = 10;
            var dialBox = new VisualElement();
            _dial = new WindDial(76f);
            dialBox.Add(_dial);
            var north = UiStyle.Text_("N", "fc-bold", "fc-dim");
            north.style.position = Position.Absolute;
            north.style.left = 0; north.style.right = 0; north.style.top = 10;
            north.style.fontSize = 9;
            north.style.unityTextAlign = TextAnchor.UpperCenter;
            dialBox.Add(north);
            wind.Add(dialBox);
            var windText = new VisualElement();
            windText.style.marginLeft = 14;
            windText.Add(UiStyle.Caption("Background wind"));
            var speedRow = UiStyle.Box("fc-row");
            speedRow.style.alignItems = Align.FlexEnd;
            _windSpeed = UiStyle.Text_(NotBuilt, "fc-mono-bold", "fc-wind-speed");
            speedRow.Add(_windSpeed);
            speedRow.Add(UiStyle.Text_("m/s", "fc-mono", "fc-dim", "fc-wind-unit"));
            windText.Add(speedRow);
            _windFrom = UiStyle.Text_("", "fc-mono");
            windText.Add(_windFrom);
            wind.Add(windText);
            page.Add(wind);

            page.Add(UiStyle.Box("fc-rule"));
            _clock = UiStyle.Readout(page, "Sim Clock");
            _elapsed = UiStyle.Readout(page, "Elapsed");
            _conditionsTimeScale = UiStyle.Readout(page, "Time scale");

            page.Add(UiStyle.Box("fc-rule"));
            _air = UiStyle.Readout(page, "Air temperature");
            _humidity = UiStyle.Readout(page, "Relative humidity");
            _sun = UiStyle.Readout(page, "Sun elevation");
            _deadMoisture = UiStyle.Readout(page, "Fuel moisture 10-h, 100-h");
            _liveMoisture = UiStyle.Readout(page, "Live fuel moisture");

            page.Add(UiStyle.Box("fc-rule"));
            _burning = UiStyle.Readout(page, "Area burning", true);
            _burnt = UiStyle.Readout(page, "Area burnt");
            _burningPlants = UiStyle.Readout(page, "Burning Plants in view");
        }

        void BuildPerformance(VisualElement page)
        {
            _fireGrid = UiStyle.Readout(page, "Fire Grid");
            _windGrid = UiStyle.Readout(page, "Wind Grid");
            _windNest = UiStyle.Readout(page, "Wind Nest");
            _plants = UiStyle.Readout(page, "Plants");
            _activeBlocks = UiStyle.Readout(page, "Active blocks");
            _burnedArea = UiStyle.Readout(page, "Burned area");
            _fireSteps = UiStyle.Readout(page, "Fire steps");
            _windSteps = UiStyle.Readout(page, "Wind steps");
            _cellUpdates = UiStyle.Readout(page, "Cell updates");
            _timeScale = UiStyle.Readout(page, "Time scale");
            _frame = UiStyle.Readout(page, "Frame");
            _memory = UiStyle.Readout(page, "Texture memory");
        }

        void Update()
        {
            _elapsedS += Time.unscaledDeltaTime;
            _frames++;
            if (_elapsedS < UpdateIntervalS) return;
            FramesPerSecond = _frames / _elapsedS;
            _elapsedS = 0f;
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
                _fireGrid.text = _windGrid.text = _windNest.text = _memory.text = _scenario.text = NotBuilt;
                return;
            }
            if (_vegetation == null) _vegetation = FindFirstObjectByType<Vegetation>();
            RefreshConditions();
            RefreshPerformance();
        }

        void RefreshConditions()
        {
            ScenarioFiles s = _app.Scenario;
            _scenario.text = s.Id;
            _terrain.text = $"{s.Grid.ExtentM / 1000f:F1} km x {s.Grid.ExtentM / 1000f:F1} km, {s.MinElevation:F0} m to {s.MaxElevation:F0} m";

            SimClock clock = _app.Clock;
            bool spinningUp = _app.Scheduler != null && _app.Scheduler.SpinningUp;
            double hour = clock.SolarHour;
            int h = (int)hour, m = (int)((hour - h) * 60.0), sec = (int)((hour - h) * 3600.0) % 60;
            _clock.text = spinningUp ? $"wind spin-up {_app.Wind.SpinUpProgress * 100f:F0} %" : $"Day {clock.Day}, {h:00}:{m:00}:{sec:00}";
            int elapsedMin = (int)(clock.Seconds / 60.0);
            _elapsed.text = $"{elapsedMin / 60} h {elapsedMin % 60:00} min";
            _conditionsTimeScale.text = clock.Playing ? $"{clock.TimeScale:F0}x, achieved {clock.AchievedTimeScale:F0}x" : $"{clock.TimeScale:F0}x, paused";

            WeatherModel weather = _app.Weather;
            if (weather != null)
            {
                bool calm = weather.WindSpeedMps < 0.05f;
                _dial.Set(weather.WindFromDeg, calm);
                _windSpeed.text = $"{weather.WindSpeedMps:F1}";
                _windFrom.text = calm ? "calm" : $"from {weather.WindFromDeg:F0} deg ({UiStyle.CompassPoint(weather.WindFromDeg)})";
                _air.text = $"{weather.ReferenceTempC:F1} deg C";
                _humidity.text = $"{weather.ReferenceRhPct:F0} %";
                float sunElevation = Mathf.Asin(Mathf.Clamp(weather.SunDirection.y, -1f, 1f)) * Mathf.Rad2Deg;
                _sun.text = $"{sunElevation:F0} deg";
                _deadMoisture.text = $"{weather.M10 * 100f:F1} %, {weather.M100 * 100f:F1} %";
                _liveMoisture.text = $"{weather.MLive * 100f:F0} %";
            }

            SpreadSolver spread = _app.Spread;
            if (spread != null)
            {
                double cellHa = GridSpec.FireCellSizeM * GridSpec.FireCellSizeM / 10000.0;
                _burning.text = $"{spread.FlamingCells * cellHa:N1} ha";
                _burnt.text = $"{spread.BurnedAreaHa:N1} ha";
            }
            _burningPlants.text = _vegetation != null && _vegetation.Visible ? $"{_vegetation.BurningDrawn:N0}" : "off";
        }

        void RefreshPerformance()
        {
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
            if (_vegetation == null || !_vegetation.Visible) _plants.text = "off";
            else
                _plants.text = $"{_vegetation.DrawnTotal:N0} of {_vegetation.PlantCount:N0} drawn\n" +
                    $"{_vegetation.DrawnPerLod[0]:N0} near, {_vegetation.DrawnPerLod[1]:N0} mid, {_vegetation.BurningDrawn:N0} burning";
            GridSpec g = _app.Scenario.Grid;
            _fireGrid.text = $"{g.FireCells} x {g.FireCells} = {g.FireCellTotal:N0} cells";
            _windGrid.text = $"{g.WindCellsX} x {GridSpec.WindCellsY} x {g.WindCellsZ} = {g.WindCellTotal:N0} cells";
            WindNest nest = _app.Nest;
            bool nested = nest != null && nest.Active;
            _windNest.text = nested
                ? $"{nest.Domain.Nx} x {nest.Domain.Ny} x {nest.Domain.Nz} cells of {nest.Domain.CellSize:F0} m"
                : _app.WindNestEnabled ? "not placed" : "off";
            // The Texture memory holds the textures of the Wind Nest too.
            _memory.text = $"{(_app.Registry.TotalBytes + (nested ? nest.TotalBytes : 0)) / (1024.0 * 1024.0):F0} MB";
        }
    }
}
