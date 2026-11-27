using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>
    /// The Control Panel: the runtime UI for Scenario, time, weather, ignition, and display controls
    /// (docs/decisions.md, decision D35). The gear button of the Tool Bar opens the Control Panel, which has three
    /// tabs. This component builds the Tool Bar, the Control Panel, and the legend of the selected Overlay.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ControlPanel : MonoBehaviour
    {
        /// <summary>The time scales that the two step buttons of the Tool Bar select.</summary>
        public static readonly float[] TimeScaleSteps = { 1f, 2f, 5f, 10f, 30f, 60f, 120f, 300f, 600f, 1000f };

        FireCAApp _app;
        CameraRig _camera;
        UIDocument _document;

        VisualElement _panel, _legend;
        UiToolToggle _gear, _igniteTool, _orbitTool;
        VisualElement _playButton;
        UiIcon _playIcon;
        Label _barTimeScale, _barDay, _barClock;
        Label _scenarioStatus;
        UiSelect _scenarioList, _overlayList;
        UiButton _modeButton;
        string _legendOverlay;

        /// <summary>The text of the Scenario status line. A failed validation check shows here.</summary>
        public string StatusText => _scenarioStatus != null ? _scenarioStatus.text : "";
        /// <summary>True while the Control Panel is open.</summary>
        public bool PanelOpen => _panel != null && _panel.style.display == DisplayStyle.Flex;

        void Start()
        {
            _app = FireCAApp.Instance;
            _camera = FindFirstObjectByType<CameraRig>();
            _document = GetComponent<UIDocument>();
            Build();
            if (_app != null)
            {
                _app.ScenarioChanged += RefreshScenario;
                _app.PointerBlocked = IsPointerOverUi;
            }
            if (_camera != null)
            {
                _camera.PointerBlocked = IsPointerOverUi;
                _camera.ModeChanged += RefreshMode;
            }
            RefreshScenario();
            RefreshLegend();
            RefreshMode();
        }

        void OnDestroy()
        {
            if (_app != null) _app.ScenarioChanged -= RefreshScenario;
            if (_camera != null) _camera.ModeChanged -= RefreshMode;
        }

        /// <summary>True when the screen position (origin at the bottom left) is over a UI element.</summary>
        public bool IsPointerOverUi(Vector2 screenPosition)
        {
            var root = _document != null ? _document.rootVisualElement : null;
            if (root == null || root.panel == null) return false;
            Vector2 p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));
            return root.panel.Pick(p) != null;
        }

        /// <summary>The names of the three tabs of the Control Panel.</summary>
        public static readonly string[] TabNames = { "Tools", "Weather, Wind, Fire", "Display" };
        /// <summary>The index of the tab on show in the Control Panel.</summary>
        public int SelectedTab => _tabs != null ? _tabs.Selected : 0;
        public void SelectTab(int index) => _tabs?.Select(index);
        UiTabs _tabs;

        /// <summary>Opens or closes the Control Panel.</summary>
        public void SetPanelOpen(bool open)
        {
            _panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            _gear.SetValueWithoutNotify(open);
        }

        void Build()
        {
            var root = _document.rootVisualElement;
            UiStyle.PrepareRoot(root);

            BuildLegend(root);
            BuildPanel(root);
            BuildBar(root);
        }

        // ---- Tool Bar ----

        void BuildBar(VisualElement root)
        {
            var bar = UiStyle.Row();
            bar.name = "tool-bar";
            bar.pickingMode = PickingMode.Ignore;
            bar.style.position = Position.Absolute;
            bar.style.left = 16; bar.style.top = 16;
            root.Add(bar);

            _gear = new UiToolToggle(UiIcon.Gear, null, false, SetPanelOpen) { name = "gear" };
            bar.Add(_gear);
            bar.Add(UiStyle.Box("fc-bar-rule"));

            _igniteTool = new UiToolToggle(UiIcon.Flame, "Ignite", true, on => _app.IgniteToolOn = on) { name = "ignite-tool" };
            bar.Add(_igniteTool);

            var time = UiStyle.Box("fc-time");
            time.name = "time-scale";
            _playButton = UiStyle.Box("fc-time-play");
            _playIcon = new UiIcon(UiIcon.Play, 24f, UiStyle.Text);
            _playButton.Add(_playIcon);
            _playButton.AddManipulator(new Clickable(() => { _app.Clock.Playing = !_app.Clock.Playing; RefreshTime(); }));
            time.Add(_playButton);
            time.Add(StepButton(UiIcon.Minus, -1));
            _barTimeScale = UiStyle.Text_("", "fc-mono-bold", "fc-time-value");
            time.Add(_barTimeScale);
            time.Add(StepButton(UiIcon.Plus, 1));
            bar.Add(time);

            _orbitTool = new UiToolToggle(UiIcon.Orbit, "Orbit", false, on => { if (_camera != null) _camera.SetAutoOrbit(on); }) { name = "orbit-tool" };
            bar.Add(_orbitTool);

            var clock = UiStyle.Box("fc-card", "fc-clock");
            _barDay = UiStyle.Text_("", "fc-caps");
            _barClock = UiStyle.Text_("", "fc-mono-bold", "fc-clock-time");
            clock.Add(_barDay);
            clock.Add(_barClock);
            bar.Add(clock);
        }

        VisualElement StepButton(string icon, int direction)
        {
            var button = UiStyle.Box("fc-time-step");
            button.Add(new UiIcon(icon, 20f, UiStyle.Text));
            button.AddManipulator(new Clickable(() => { _app.Clock.SetTimeScale(NextTimeScale(_app.Clock.TimeScale, direction)); RefreshTime(); }));
            return button;
        }

        /// <summary>The next time scale of TimeScaleSteps above the value (direction 1) or below it (direction -1).</summary>
        public static float NextTimeScale(float current, int direction)
        {
            if (direction > 0)
            {
                foreach (float step in TimeScaleSteps) if (step > current * 1.001f) return step;
                return TimeScaleSteps[TimeScaleSteps.Length - 1];
            }
            for (int i = TimeScaleSteps.Length - 1; i >= 0; i--) if (TimeScaleSteps[i] < current * 0.999f) return TimeScaleSteps[i];
            return TimeScaleSteps[0];
        }

        // ---- Control Panel ----

        void BuildPanel(VisualElement root)
        {
            _panel = UiStyle.Box("fc-card", "fc-panel");
            _panel.name = "control-panel";
            _panel.style.display = DisplayStyle.None;
            root.Add(_panel);

            var head = UiStyle.Box("fc-panel-head");
            head.Add(UiStyle.Text_("Control Panel", "fc-bold", "fc-panel-title"));
            var close = UiStyle.Box("fc-close");
            close.Add(new UiIcon(UiIcon.Close, 20f, UiStyle.DimText));
            close.AddManipulator(new Clickable(() => SetPanelOpen(false)));
            head.Add(close);
            _panel.Add(head);

            var tabs = new UiTabs { name = "control-tabs" };
            _tabs = tabs;
            _panel.Add(tabs);
            BuildToolsTab(tabs.AddTab(TabNames[0], UiIcon.Tools, "fc-columns"));
            BuildWeatherTab(tabs.AddTab(TabNames[1], UiIcon.Weather, "fc-columns"));
            BuildDisplayTab(tabs.AddTab(TabNames[2], UiIcon.Display, "fc-columns"));
        }

        static VisualElement[] Columns(VisualElement page, int count)
        {
            var columns = new VisualElement[count];
            for (int i = 0; i < count; i++)
            {
                if (i > 0) page.Add(UiStyle.Box("fc-column-gap"));
                columns[i] = UiStyle.Box("fc-column");
                page.Add(columns[i]);
            }
            return columns;
        }

        static void Group(VisualElement column, string title)
        {
            if (column.childCount > 0) column.Add(UiStyle.Box("fc-group-gap"));
            column.Add(UiStyle.Caption(title));
        }

        static string Fixed0(float v) => v.ToString("F0");
        static string Fixed2(float v) => v.ToString("F2");
        static string Percent01(float v) => (v * 100f).ToString("F0") + " %";

        UiSlider Slider(VisualElement parent, string label, string name, float low, float high, float value,
            Func<float, string> format, Action<float> onChange, float step = 0f)
        {
            var slider = new UiSlider(label, low, high, value, format, onChange, step) { name = name };
            parent.Add(slider);
            return slider;
        }

        UiSwitch Switch(VisualElement parent, string label, string name, bool value, Action<bool> onChange, bool fire = false)
        {
            var toggle = new UiSwitch(label, value, onChange, fire) { name = name };
            parent.Add(toggle);
            return toggle;
        }

        // ---- Tools tab: Scenario, ignition, time, camera, slice tool ----

        DemoRun _demo;
        UiButton _demoButton, _playTextButton, _pauseTextButton;
        Label _clockReadout;
        UiSlider _timeScale, _orbitPeriod;
        UiSwitch _igniteSwitch, _orbitSwitch;
        Label _sliceRange;

        void BuildToolsTab(VisualElement page)
        {
            var columns = Columns(page, 3);

            // Scenario
            Group(columns[0], "Scenario");
            var ids = new List<string>(ScenarioLoader.ListScenarios());
            _scenarioList = new UiSelect(ids, ids.Count > 0 ? 0 : -1, null) { name = "scenario-list" };
            columns[0].Add(_scenarioList);
            columns[0].Add(UiButton.RowOf(
                new UiButton("Load", () => { if (!string.IsNullOrEmpty(_scenarioList.Value)) _app.LoadScenario(_scenarioList.Value); }, true),
                new UiButton("Reset", () => _app.ResetScenario())));
            _demo = FindFirstObjectByType<DemoRun>();
            if (_demo != null)
            {
                _demoButton = new UiButton("Start demo run (F5)", () => { if (_demo.Running) _demo.StopDemo(); else _demo.StartDemo(); }) { name = "demo-run" };
                columns[0].Add(UiButton.RowOf(_demoButton));
            }
            _scenarioStatus = UiStyle.Text_("", "fc-help");
            _scenarioStatus.name = "scenario-status";
            columns[0].Add(_scenarioStatus);

            // Ignition
            Group(columns[0], "Ignition");
            _igniteSwitch = Switch(columns[0], "Ignite tool", "ignite-switch", false, on => _app.IgniteToolOn = on, true);
            columns[0].Add(UiStyle.Text_("While the ignite tool is on, a left click on the terrain ignites the cells within 6 m.", "fc-help"));
            columns[0].Add(UiButton.RowOf(new UiButton("Clear Fire", () => _app.ClearFire())));

            // Time
            Group(columns[1], "Time");
            _clockReadout = UiStyle.Text_("", "fc-mono-bold");
            _clockReadout.name = "clock-readout";
            _clockReadout.style.fontSize = 15;
            _clockReadout.style.marginTop = 8;
            columns[1].Add(_clockReadout);
            _playTextButton = new UiButton("Play", () => { _app.Clock.Playing = true; RefreshTime(); });
            _pauseTextButton = new UiButton("Pause", () => { _app.Clock.Playing = false; RefreshTime(); });
            columns[1].Add(UiButton.RowOf(_playTextButton, _pauseTextButton));
            // The slider is logarithmic: the slider value is log10 of the time scale, from 1 to 1000.
            _timeScale = Slider(columns[1], "Time scale", "time-scale-slider", 0f, 3f, Mathf.Log10(SimClock.DefaultTimeScale),
                v => Mathf.Pow(10f, v).ToString("F0") + "x",
                v => { _app.Clock.SetTimeScale(Mathf.Pow(10f, v)); RefreshTime(); });

            // Camera
            Group(columns[1], "Camera");
            _orbitSwitch = Switch(columns[1], "Orbit the look-at point", "orbit-switch", false, on => { if (_camera != null) _camera.SetAutoOrbit(on); });
            _orbitPeriod = Slider(columns[1], "Time for one orbit (Sim Clock)", "orbit-period",
                CameraRig.MinOrbitPeriodHours, CameraRig.MaxOrbitPeriodHours,
                _camera != null ? _camera.OrbitPeriodHours : CameraRig.DefaultOrbitPeriodHours,
                v => v.ToString("F2") + " h", v => { if (_camera != null) _camera.OrbitPeriodHours = v; }, 0.25f);
            for (int r = 0; r < 2; r++)
            {
                int first = r * 2;
                columns[1].Add(UiButton.RowOf(
                    new UiButton(CameraRig.PresetNames[first], () => { if (_camera != null) _camera.ApplyPreset(first); }),
                    new UiButton(CameraRig.PresetNames[first + 1], () => { if (_camera != null) _camera.ApplyPreset(first + 1); })));
            }
            _modeButton = new UiButton("", () => { if (_camera != null) _camera.ToggleMode(); });
            columns[1].Add(UiButton.RowOf(_modeButton));
            columns[1].Add(UiStyle.Text_(
                "Orbit: right drag rotates, middle drag pans, wheel zooms. " +
                "Fly: right drag looks, W A S D Q E move, Shift is fast. Tab switches the mode. Keys 1 to 4 select a preset.", "fc-help"));

            // Slice tool
            Group(columns[2], "Slice tool");
            var slice = FindFirstObjectByType<SlicePlane>();
            if (slice != null)
            {
                Switch(columns[2], "Slice plane", "slice-plane", slice.Visible, on => slice.SetVisible(on));
                var field = new UiSelect(new List<string>(SlicePlane.FieldNames), (int)slice.SelectedField, index =>
                {
                    slice.SetField((SlicePlane.Field)index);
                    _sliceRange.text = "Range: " + SlicePlane.FieldRanges[index] + ".";
                }) { name = "slice-field" };
                columns[2].Add(field);
                _sliceRange = UiStyle.Text_("Range: " + SlicePlane.FieldRanges[(int)slice.SelectedField] + ".", "fc-help");
                columns[2].Add(_sliceRange);
                columns[2].Add(new UiSelect(new List<string> { "East-west cut", "North-south cut" }, slice.EastWestCut ? 0 : 1,
                    index => slice.SetEastWestCut(index == 0)) { name = "slice-cut" });
                Slider(columns[2], "Position", "slice-position", 0f, 1f, slice.Position01, Percent01, v => slice.SetPosition(v));
            }
        }

        // ---- Weather, Wind, Fire tab ----

        UiSlider _windSpeed, _windDirection, _tempMax, _tempMin, _rhMin, _couplingGain, _liveMoisture;
        UiSwitch _useScenarioWeather;
        Label _weatherReadout;

        void BuildWeatherTab(VisualElement page)
        {
            var columns = Columns(page, 3);

            Group(columns[0], "Weather");
            _weatherReadout = UiStyle.Text_("", "fc-help");
            _weatherReadout.name = "weather-readout";
            columns[0].Add(_weatherReadout);
            _useScenarioWeather = Switch(columns[0], "Use Scenario weather", "use-scenario-weather", true, on =>
            {
                WeatherModel weather = _app.Weather;
                if (weather == null) return;
                // On: the Scenario values return. Off: the current values become manual values.
                if (on) weather.RestoreScenarioWeather();
                else ApplyManualWeather();
            });
            _windSpeed = Slider(columns[0], "Background wind speed", "wind-speed", 0f, 20f, 0f, v => v.ToString("F1") + " m/s", v => ApplyManualWeather());
            _windDirection = Slider(columns[0], "Background wind direction (from)", "wind-direction", 0f, 360f, 0f,
                v => v.ToString("F0") + " deg (" + UiStyle.CompassPoint(v) + ")", v => ApplyManualWeather());
            _tempMax = Slider(columns[0], "tempMaxC", "temp-max", 0f, 50f, 0f, v => v.ToString("F1") + " deg C", v => ApplyManualWeather());
            _tempMin = Slider(columns[0], "tempMinC", "temp-min", -10f, 40f, 0f, v => v.ToString("F1") + " deg C", v => ApplyManualWeather());
            _rhMin = Slider(columns[0], "rhMinPct", "rh-min", 2f, 80f, 0f, v => v.ToString("F0") + " %", v => ApplyManualWeather());

            // The Wind Solver (docs/decisions.md, decision D31) and the Wind Nest (decision D32).
            Group(columns[1], "Wind Solver");
            Slider(columns[1], "Advection sharpness (0 is the smooth advection)", "advection-sharpness", 0f, 1f,
                _app.AdvectionSharpness, Fixed2, v => _app.AdvectionSharpness = v);
            Slider(columns[1], "Vorticity confinement (0 turns the force off)", "vorticity-confinement", 0f, WindSolver.MaxVorticityConfinement,
                _app.VorticityConfinement, v => v.ToString("F3"), v => _app.VorticityConfinement = v);
            Group(columns[1], "Wind Nest");
            Switch(columns[1], "Wind Nest (finer wind around the fire)", "wind-nest", _app.WindNestEnabled, on => _app.WindNestEnabled = on);
            Slider(columns[1], "Cell size, for the next placement", "wind-nest-cell-size", WindNest.MinCellSizeM, WindNest.MaxCellSizeM,
                _app.WindNestCellSizeM, v => WindNest.SnapCellSize(v).ToString("F0") + " m", v => _app.WindNestCellSizeM = v);
            columns[1].Add(UiButton.RowOf(new UiButton("Move the Wind Nest to the view", () =>
            {
                if (_camera != null) _app.PlaceWindNest(new Vector2(_camera.Target.x, _camera.Target.z));
            }) { name = "wind-nest-move" }));

            Group(columns[2], "Fire");
            _couplingGain = Slider(columns[2], "couplingGain (0 turns fire coupling off)", "coupling-gain", 0f, 2f, _app.CouplingGain,
                Fixed2, v => _app.CouplingGain = v);
            _liveMoisture = Slider(columns[2], "Live fuel moisture", "live-moisture", 30f, 300f, 100f, v => v.ToString("F0") + " %",
                v => { if (_app.Weather != null) _app.Weather.SetLiveMoisture(v / 100f); });
        }

        // A manual weather value overrides the Scenario value until the user turns "Use Scenario weather" on.
        void ApplyManualWeather()
        {
            WeatherModel weather = _app != null ? _app.Weather : null;
            if (weather == null) return;
            weather.SetManualWind(_windSpeed.Value, _windDirection.Value);
            weather.SetManualTemperature(_tempMax.Value, _tempMin.Value);
            weather.SetManualRhMin(_rhMin.Value);
        }

        void RefreshWeather()
        {
            WeatherModel weather = _app != null ? _app.Weather : null;
            if (weather == null || _weatherReadout == null) return;
            _useScenarioWeather.SetValueWithoutNotify(weather.UseScenarioWeather);
            _windSpeed.Sync(weather.WindSpeedMps);
            _windDirection.Sync(weather.WindFromDeg);
            _tempMax.Sync(weather.TempMaxC);
            _tempMin.Sync(weather.TempMinC);
            _rhMin.Sync(weather.RhMinPct);
            _liveMoisture.Sync(weather.MLive * 100f);
            _couplingGain.Sync(_app.CouplingGain);
            float sunElevation = Mathf.Asin(Mathf.Clamp(weather.SunDirection.y, -1f, 1f)) * Mathf.Rad2Deg;
            _weatherReadout.text =
                $"Air {weather.ReferenceTempC:F1} deg C, humidity {weather.ReferenceRhPct:F0} percent, sun elevation {sunElevation:F0} degrees. " +
                $"Fuel moisture: 10-h {weather.M10 * 100f:F1}, 100-h {weather.M100 * 100f:F1}, live {weather.MLive * 100f:F0} percent.";
        }

        // ---- Display tab ----

        void BuildDisplayTab(VisualElement page)
        {
            var columns = Columns(page, 3);

            Group(columns[0], "Overlay");
            var names = new List<string> { Overlays.NoneName };
            foreach (var overlay in Overlays.Catalog)
                if (overlay.Available) names.Add(overlay.Name);
            _overlayList = new UiSelect(names, 0, index =>
            {
                _app.Overlays.Select(Overlays.Find(names[index]));
                RefreshLegend();
            }) { name = "overlay-list" };
            columns[0].Add(_overlayList);
            Slider(columns[0], "Overlay opacity", "overlay-opacity", 0f, 1f, Overlays.DefaultOpacity, Percent01, v => _app.Overlays.SetOpacity(v));
            Slider(columns[0], "Fuel color smoothing", "fuel-color-smoothing", 0f, Overlays.MaxFuelColorSmoothingM,
                _app.Overlays.FuelColorSmoothingM, v => v.ToString("F0") + " m", v => _app.Overlays.SetFuelColorSmoothing(v));
            // The slider has an effect when the Scenario has a Ground Image.
            Slider(columns[0], "Ground Image opacity", "ground-image-opacity", 0f, 1f,
                _app.Overlays.GroundImageOpacity, Percent01, v => _app.Overlays.SetGroundImageOpacity(v));

            var vegetation = FindFirstObjectByType<Vegetation>();
            if (vegetation != null)
            {
                Group(columns[0], "Vegetation");
                Switch(columns[0], "Vegetation", "vegetation", vegetation.Visible, on => vegetation.Visible = on);
                Slider(columns[0], "Vegetation density", "vegetation-density", 0f, 1f, vegetation.Density, Percent01, v => vegetation.Density = v);
                Slider(columns[0], "Vegetation draw distance", "vegetation-distance", Vegetation.MinDrawDistanceM, Vegetation.MaxDrawDistanceM,
                    vegetation.DrawDistanceM, v => v.ToString("F0") + " m", v => vegetation.DrawDistanceM = v);
                Switch(columns[0], "Flames and smoke on burning plants", "vegetation-effects", vegetation.EffectsVisible, on => vegetation.EffectsVisible = on);
                Switch(columns[0], "Plant shadows", "vegetation-shadows", vegetation.CastShadows, on => vegetation.CastShadows = on);
            }

            Group(columns[1], "Smoke");
            Switch(columns[1], "Smoke", "smoke", _app.SmokeVisible, on => _app.SmokeVisible = on);
            Slider(columns[1], "Smoke density", "smoke-density", 0f, FireCAApp.MaxSmokeDensity, _app.SmokeDensity, Fixed2, v => _app.SmokeDensity = v);
            Slider(columns[1], "Smoke darkness", "smoke-darkness", 0f, 1f, _app.SmokeDarkness, Fixed2, v => _app.SmokeDarkness = v);
            Slider(columns[1], "Smoke billows", "smoke-billows", 0f, 1f, _app.SmokeBillows, Fixed2, v => _app.SmokeBillows = v);
            Slider(columns[1], "Turbulence in smoke and particles", "turbulence-gain", 0f, FireCAApp.MaxTurbulenceGain,
                _app.TurbulenceGain, Fixed2, v => _app.TurbulenceGain = v);

            var particles = FindFirstObjectByType<WindParticles>();
            if (particles != null)
            {
                Group(columns[2], "Wind Particles");
                Switch(columns[2], "Wind Particles", "wind-particles", particles.Visible, on => particles.Visible = on);
                Slider(columns[2], "Particle count", "wind-particle-count", WindParticles.MinCount, WindParticles.MaxCount, particles.Count,
                    v => v.ToString("N0"), v => particles.SetCount(Mathf.RoundToInt(v)), 100000f);
                Slider(columns[2], "Particle size", "wind-particle-size", 0f, WindParticles.MaxSizeM, particles.SizeM,
                    v => v.ToString("F1") + " m", v => particles.SetSize(v));
            }
        }

        // ---- legend of the selected Overlay, on the main screen ----

        void BuildLegend(VisualElement root)
        {
            _legend = UiStyle.Box("fc-card", "fc-legend");
            _legend.name = "overlay-legend";
            _legend.pickingMode = PickingMode.Ignore;
            _legend.style.display = DisplayStyle.None;
            root.Add(_legend);
        }

        void RefreshLegend()
        {
            if (_legend == null || _app == null) return;
            _legend.Clear();
            OverlayInfo overlay = _app.Overlays.Selected;
            _legendOverlay = overlay != null ? overlay.Name : Overlays.NoneName;
            _legend.style.display = overlay != null ? DisplayStyle.Flex : DisplayStyle.None;
            if (overlay == null) return;

            _legend.Add(UiStyle.Caption("Overlay"));
            _legend.Add(UiStyle.Text_(overlay.Name, "fc-bold", "fc-legend-name"));
            string rangeText = overlay.RangeText;
            if (overlay.Name == "Air temperature" && _app.Weather != null)
            {
                // The range of the Air temperature Overlay follows the weather inputs.
                Vector2 range = Overlays.AirTemperatureRange(_app.Weather);
                rangeText = $"{range.x:F0} to {range.y:F0}";
            }

            var unitLine = UiStyle.Text_($"Unit: {overlay.Unit}. Range: {rangeText}.", "fc-small", "fc-dim");
            unitLine.style.marginBottom = 4;
            _legend.Add(unitLine);

            if (overlay.Categorical)
            {
                for (int code = 1; code <= FuelCodes.FuelModelCount; code++)
                    _legend.Add(Swatch(Overlays.CategoricalColor(code), $"{code,2}  {FuelCodes.NameOf(code)}"));
                _legend.Add(Swatch(Overlays.CategoricalNotBurnable, "Not burnable"));
            }
            else
            {
                var bar = new Image { image = _app.Overlays.ColorMap, scaleMode = ScaleMode.StretchToFill };
                bar.AddToClassList("fc-legend-bar");
                _legend.Add(bar);
                var ends = UiStyle.Row("fc-between");
                ends.style.marginTop = 4;
                string[] range = rangeText.Split(new[] { " to " }, StringSplitOptions.None);
                // The color map of the Arrival time Overlay spans 6 simulated hours, then repeats.
                ends.Add(UiStyle.Text_(range.Length == 2 ? range[0] : "0 h", "fc-mono", "fc-small", "fc-dim"));
                ends.Add(UiStyle.Text_(overlay.Unit, "fc-mono", "fc-small", "fc-dim"));
                ends.Add(UiStyle.Text_(range.Length == 2 ? range[1].Split(',')[0] : "6 h", "fc-mono", "fc-small", "fc-dim"));
                _legend.Add(ends);
            }
            foreach (VisualElement child in _legend.Query<VisualElement>().Build()) child.pickingMode = PickingMode.Ignore;
        }

        static VisualElement Swatch(Color color, string text)
        {
            var row = UiStyle.Box("fc-swatch-row");
            var box = UiStyle.Box("fc-swatch");
            box.style.backgroundColor = color;
            row.Add(box);
            row.Add(UiStyle.Text_(text, "fc-small"));
            return row;
        }

        // ---- refresh ----

        void RefreshTime()
        {
            if (_app == null || _clockReadout == null) return;
            SimClock clock = _app.Clock;
            // The spin-up progress readout. The Sim Clock does not start before the spin-up ends.
            bool spinningUp = _app.Scheduler != null && _app.Scheduler.SpinningUp;
            double hour = clock.SolarHour;
            int h = (int)hour, m = (int)((hour - h) * 60.0), s = (int)((hour - h) * 3600.0) % 60;
            if (spinningUp)
            {
                string percent = $"{_app.Wind.SpinUpProgress * 100f:F0} %";
                _clockReadout.text = "Wind spin-up: " + percent;
                _barDay.text = "WIND SPIN-UP";
                _barClock.text = percent;
            }
            else
            {
                _clockReadout.text = clock.Readout;
                _barDay.text = $"DAY {clock.Day}";
                _barClock.text = $"{h:00}:{m:00}:{s:00}";
            }
            _barTimeScale.text = $"{clock.TimeScale:F0}x";
            _timeScale.Sync(Mathf.Log10(clock.TimeScale));
            _playTextButton.SetEnabled(!clock.Playing && !spinningUp);
            _pauseTextButton.SetEnabled(clock.Playing);
            _playButton.SetEnabled(!spinningUp);
            _playButton.EnableInClassList("fc-time-play--on", clock.Playing);
            _playIcon.Set(clock.Playing ? UiIcon.Pause : UiIcon.Play, clock.Playing ? UiStyle.Wind : spinningUp ? UiStyle.Off : UiStyle.Text);
        }

        void Update()
        {
            if (_app == null || _panel == null) return;
            // The Sim Clock, the tools, and the weather can change outside the Control Panel, so the readouts refresh in each frame.
            RefreshTime();
            RefreshWeather();
            _igniteTool.SetValueWithoutNotify(_app.IgniteToolOn);
            _igniteSwitch.SetValueWithoutNotify(_app.IgniteToolOn);
            bool orbit = _camera != null && _camera.AutoOrbit;
            _orbitTool.SetValueWithoutNotify(orbit);
            _orbitSwitch.SetValueWithoutNotify(orbit);
            if (_camera != null) _orbitPeriod.Sync(_camera.OrbitPeriodHours);
            // The demo run selects an Overlay outside the Control Panel.
            string selected = _app.Overlays.Selected != null ? _app.Overlays.Selected.Name : Overlays.NoneName;
            if (_legendOverlay != selected)
            {
                _overlayList.SetValueWithoutNotify(selected);
                RefreshLegend();
            }
            if (_demoButton != null && _demo != null) _demoButton.Text = _demo.Running ? "Stop demo run (F5)" : "Start demo run (F5)";
        }

        void RefreshScenario()
        {
            if (_app == null || _scenarioStatus == null) return;
            RefreshTime();
            if (_app.LastError != null)
            {
                _scenarioStatus.text = _app.LastError;
                _scenarioStatus.AddToClassList("fc-error");
                return;
            }
            _scenarioStatus.RemoveFromClassList("fc-error");
            if (_app.Scenario == null)
            {
                _scenarioStatus.text = "No Scenario is loaded.";
                return;
            }
            var s = _app.Scenario;
            _scenarioList.SetValueWithoutNotify(s.Id);
            _scenarioStatus.text =
                $"{s.Id}: {s.Grid.FireCells} x {s.Grid.FireCells} cells, elevation {s.MinElevation:F0} m to {s.MaxElevation:F0} m. " +
                $"Loaded in {_app.LastLoadSeconds:F2} s.";
        }

        void RefreshMode()
        {
            if (_modeButton == null) return;
            var mode = _camera != null ? _camera.CurrentMode : CameraRig.Mode.Orbit;
            _modeButton.Text = mode == CameraRig.Mode.Orbit ? "Camera mode: Orbit" : "Camera mode: Fly";
        }
    }
}
