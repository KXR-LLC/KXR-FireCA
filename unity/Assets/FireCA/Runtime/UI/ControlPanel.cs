using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>
    /// The Control Panel: the runtime UI for Scenario, time, weather, ignition, and display controls.
    /// M0 builds the Scenario section and the Display section. Later milestones add the other sections.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ControlPanel : MonoBehaviour
    {
        const float PanelWidth = 300f;

        FireCAApp _app;
        CameraRig _camera;
        UIDocument _document;

        VisualElement _panel;
        Button _expandButton;
        DropdownField _scenarioList;
        Label _scenarioStatus;
        DropdownField _overlayList;
        Slider _opacity;
        VisualElement _legend;
        Button _modeButton;

        /// <summary>The text of the Scenario status line. A failed validation check shows here.</summary>
        public string StatusText => _scenarioStatus != null ? _scenarioStatus.text : "";

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

        void Build()
        {
            var root = _document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.style.flexGrow = 1;

            _panel = new VisualElement { name = "control-panel" };
            _panel.style.position = Position.Absolute;
            _panel.style.left = 0; _panel.style.top = 0; _panel.style.bottom = 0;
            _panel.style.width = PanelWidth;
            _panel.style.backgroundColor = UiStyle.PanelBackground;
            _panel.style.borderRightWidth = 1;
            _panel.style.borderRightColor = UiStyle.Line;
            root.Add(_panel);

            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.contentContainer.style.paddingLeft = 14;
            scroll.contentContainer.style.paddingRight = 14;
            scroll.contentContainer.style.paddingTop = 12;
            scroll.contentContainer.style.paddingBottom = 16;
            _panel.Add(scroll);

            var titleRow = UiStyle.Row();
            titleRow.style.justifyContent = Justify.SpaceBetween;
            var title = UiStyle.Text_("KXR-FireCA", 16, true);
            titleRow.Add(title);
            var collapse = new Button(() => SetCollapsed(true)) { text = "Hide" };
            collapse.style.height = 22;
            collapse.style.fontSize = 11;
            titleRow.Add(collapse);
            scroll.Add(titleRow);
            scroll.Add(UiStyle.Text_("Control Panel", 11, false, UiStyle.DimText));

            _expandButton = new Button(() => SetCollapsed(false)) { text = "Control Panel" };
            _expandButton.style.position = Position.Absolute;
            _expandButton.style.left = 8; _expandButton.style.top = 8;
            _expandButton.style.height = 24;
            _expandButton.style.display = DisplayStyle.None;
            root.Add(_expandButton);

            BuildScenarioSection(scroll);
            BuildTimeSection(scroll);
            BuildIgnitionSection(scroll);
            BuildWeatherSection(scroll);
            BuildFireSection(scroll);
            BuildWindSolverSection(scroll);
            BuildDisplaySection(scroll);
        }

        // ---- Fire ----

        Slider _couplingGain, _liveMoisture;

        void BuildFireSection(VisualElement parent)
        {
            var section = UiStyle.Section("Fire");
            parent.Add(section);
            _couplingGain = WeatherSlider(section, "couplingGain (0 turns fire coupling off)", 0f, 2f,
                () => _app.CouplingGain = _couplingGain.value);
            _couplingGain.name = "coupling-gain";
            _couplingGain.SetValueWithoutNotify(_app.CouplingGain);
            _liveMoisture = WeatherSlider(section, "Live fuel moisture (percent)", 30f, 300f,
                () => { if (_app.Weather != null) _app.Weather.SetLiveMoisture(_liveMoisture.value / 100f); });
            _liveMoisture.name = "live-moisture";
        }

        // ---- Wind Solver (docs/decisions.md, decision D31) ----

        void BuildWindSolverSection(VisualElement parent)
        {
            var section = UiStyle.Section("Wind Solver");
            parent.Add(section);
            DisplaySlider(section, "Advection sharpness (0 is the smooth advection)", "advection-sharpness", 0f, 1f,
                _app.AdvectionSharpness, v => _app.AdvectionSharpness = v);
            DisplaySlider(section, "Vorticity confinement (0 turns the force off)", "vorticity-confinement", 0f, WindSolver.MaxVorticityConfinement,
                _app.VorticityConfinement, v => _app.VorticityConfinement = v);
        }

        // ---- Weather ----

        Slider _windSpeed, _windDirection, _tempMax, _tempMin, _rhMin;
        Toggle _useScenarioWeather;
        Label _weatherReadout;

        Slider WeatherSlider(VisualElement parent, string label, float low, float high, System.Action onChange)
        {
            var text = UiStyle.Text_(label, 11, false, UiStyle.DimText);
            text.style.marginTop = 4;
            parent.Add(text);
            var slider = new Slider(low, high) { showInputField = true };
            slider.style.marginLeft = 0; slider.style.marginRight = 0;
            slider.RegisterValueChangedCallback(e => onChange());
            parent.Add(slider);
            return slider;
        }

        void BuildWeatherSection(VisualElement parent)
        {
            var section = UiStyle.Section("Weather");
            parent.Add(section);

            _weatherReadout = UiStyle.Text_("", 11);
            _weatherReadout.name = "weather-readout";
            section.Add(_weatherReadout);

            _useScenarioWeather = new Toggle("Use Scenario weather") { value = true, name = "use-scenario-weather" };
            _useScenarioWeather.style.marginLeft = 0;
            _useScenarioWeather.style.marginTop = 4;
            _useScenarioWeather.labelElement.style.color = UiStyle.Text;
            _useScenarioWeather.labelElement.style.fontSize = 12;
            _useScenarioWeather.RegisterValueChangedCallback(e =>
            {
                WeatherModel weather = _app.Weather;
                if (weather == null) return;
                // On: the Scenario values return. Off: the current values become manual values.
                if (e.newValue) weather.RestoreScenarioWeather();
                else ApplyManualWeather();
            });
            section.Add(_useScenarioWeather);

            _windSpeed = WeatherSlider(section, "Background wind speed (m/s)", 0f, 20f, ApplyManualWeather);
            _windDirection = WeatherSlider(section, "Background wind direction (degrees, from)", 0f, 360f, ApplyManualWeather);
            _tempMax = WeatherSlider(section, "tempMaxC (deg C)", 0f, 50f, ApplyManualWeather);
            _tempMin = WeatherSlider(section, "tempMinC (deg C)", -10f, 40f, ApplyManualWeather);
            _rhMin = WeatherSlider(section, "rhMinPct (percent)", 2f, 80f, ApplyManualWeather);
        }

        // A manual weather value overrides the Scenario value until the user turns "Use Scenario weather" on.
        void ApplyManualWeather()
        {
            WeatherModel weather = _app != null ? _app.Weather : null;
            if (weather == null) return;
            weather.SetManualWind(_windSpeed.value, _windDirection.value);
            weather.SetManualTemperature(_tempMax.value, _tempMin.value);
            weather.SetManualRhMin(_rhMin.value);
        }

        void RefreshWeather()
        {
            WeatherModel weather = _app != null ? _app.Weather : null;
            if (weather == null || _weatherReadout == null) return;
            _useScenarioWeather.SetValueWithoutNotify(weather.UseScenarioWeather);
            _windSpeed.SetValueWithoutNotify(weather.WindSpeedMps);
            _windDirection.SetValueWithoutNotify(weather.WindFromDeg);
            _tempMax.SetValueWithoutNotify(weather.TempMaxC);
            _tempMin.SetValueWithoutNotify(weather.TempMinC);
            _rhMin.SetValueWithoutNotify(weather.RhMinPct);
            if (_liveMoisture != null) _liveMoisture.SetValueWithoutNotify(weather.MLive * 100f);
            if (_couplingGain != null) _couplingGain.SetValueWithoutNotify(_app.CouplingGain);
            float sunElevation = Mathf.Asin(Mathf.Clamp(weather.SunDirection.y, -1f, 1f)) * Mathf.Rad2Deg;
            _weatherReadout.text =
                $"Air {weather.ReferenceTempC:F1} deg C, humidity {weather.ReferenceRhPct:F0} percent, sun elevation {sunElevation:F0} degrees. " +
                $"Fuel moisture: 10-h {weather.M10 * 100f:F1}, 100-h {weather.M100 * 100f:F1}, live {weather.MLive * 100f:F0} percent.";
        }

        // ---- Time ----

        Label _clockReadout, _timeScaleLabel;
        Slider _timeScale;
        Button _playButton, _pauseButton;
        Toggle _igniteTool;

        void BuildTimeSection(VisualElement parent)
        {
            var section = UiStyle.Section("Time");
            parent.Add(section);

            _clockReadout = UiStyle.Text_("", 13, true);
            _clockReadout.name = "clock-readout";
            section.Add(_clockReadout);

            var row = UiStyle.Row();
            row.style.marginTop = 4;
            _playButton = UiStyle.Button("Play", () => { _app.Clock.Playing = true; RefreshTime(); });
            _pauseButton = UiStyle.Button("Pause", () => { _app.Clock.Playing = false; RefreshTime(); });
            row.Add(_playButton);
            row.Add(_pauseButton);
            section.Add(row);

            _timeScaleLabel = UiStyle.Text_("", 11, false, UiStyle.DimText);
            _timeScaleLabel.style.marginTop = 6;
            section.Add(_timeScaleLabel);
            // The slider is logarithmic: the slider value is log10 of the time scale, from 1 to 1000.
            _timeScale = new Slider(0f, 3f) { value = Mathf.Log10(SimClock.DefaultTimeScale), name = "time-scale" };
            _timeScale.style.marginLeft = 0; _timeScale.style.marginRight = 0;
            _timeScale.RegisterValueChangedCallback(e =>
            {
                _app.Clock.SetTimeScale(Mathf.Pow(10f, e.newValue));
                RefreshTime();
            });
            section.Add(_timeScale);
        }

        void RefreshTime()
        {
            if (_app == null || _clockReadout == null) return;
            SimClock clock = _app.Clock;
            // The spin-up progress readout. The Sim Clock does not start before the spin-up ends.
            bool spinningUp = _app.Scheduler != null && _app.Scheduler.SpinningUp;
            _clockReadout.text = spinningUp
                ? $"Wind spin-up: {_app.Wind.SpinUpProgress * 100f:F0} percent"
                : clock.Readout;
            _timeScaleLabel.text = $"Time scale: {clock.TimeScale:F0}x";
            _timeScale.SetValueWithoutNotify(Mathf.Log10(clock.TimeScale));
            _playButton.SetEnabled(!clock.Playing && !spinningUp);
            _pauseButton.SetEnabled(clock.Playing);
        }

        void Update()
        {
            // The Sim Clock and the time scale can change outside the Control Panel, so the readouts refresh in each frame.
            RefreshTime();
            RefreshWeather();
            // The demo run selects an Overlay outside the Control Panel.
            string selected = _app != null && _app.Overlays.Selected != null ? _app.Overlays.Selected.Name : Overlays.NoneName;
            if (_overlayList != null && _overlayList.value != selected)
            {
                _overlayList.SetValueWithoutNotify(selected);
                RefreshLegend();
            }
            if (_demoButton != null && _demo != null) _demoButton.text = _demo.Running ? "Stop demo run (F5)" : "Start demo run (F5)";
        }

        // ---- Ignition ----

        void BuildIgnitionSection(VisualElement parent)
        {
            var section = UiStyle.Section("Ignition");
            parent.Add(section);

            _igniteTool = new Toggle("Ignite tool") { name = "ignite-tool" };
            _igniteTool.style.marginLeft = 0;
            _igniteTool.labelElement.style.color = UiStyle.Text;
            _igniteTool.labelElement.style.fontSize = 12;
            _igniteTool.RegisterValueChangedCallback(e => _app.IgniteToolOn = e.newValue);
            section.Add(_igniteTool);
            var help = UiStyle.Text_("While the ignite tool is on, a left click on the terrain ignites the cells within 6 m.",
                10, false, UiStyle.DimText);
            section.Add(help);

            var row = UiStyle.Row();
            row.style.marginTop = 4;
            row.Add(UiStyle.Button("Clear Fire", () => _app.ClearFire()));
            section.Add(row);
        }

        void SetCollapsed(bool collapsed)
        {
            _panel.style.display = collapsed ? DisplayStyle.None : DisplayStyle.Flex;
            _expandButton.style.display = collapsed ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ---- Scenario ----

        void BuildScenarioSection(VisualElement parent)
        {
            var section = UiStyle.Section("Scenario");
            parent.Add(section);

            var ids = new List<string>(ScenarioLoader.ListScenarios());
            _scenarioList = new DropdownField(ids, ids.Count > 0 ? 0 : -1) { name = "scenario-list" };
            _scenarioList.style.marginLeft = 0; _scenarioList.style.marginRight = 0;
            section.Add(_scenarioList);

            var row = UiStyle.Row();
            row.style.marginTop = 4;
            row.Add(UiStyle.Button("Load", () => { if (!string.IsNullOrEmpty(_scenarioList.value)) _app.LoadScenario(_scenarioList.value); }));
            row.Add(UiStyle.Button("Reset", () => _app.ResetScenario()));
            section.Add(row);

            _demo = FindFirstObjectByType<DemoRun>();
            if (_demo != null)
            {
                var demoRow = UiStyle.Row();
                demoRow.style.marginTop = 4;
                _demoButton = UiStyle.Button("Start demo run (F5)", () => { if (_demo.Running) _demo.StopDemo(); else _demo.StartDemo(); });
                _demoButton.name = "demo-run";
                demoRow.Add(_demoButton);
                section.Add(demoRow);
            }

            _scenarioStatus = UiStyle.Text_("", 11, false, UiStyle.DimText);
            _scenarioStatus.name = "scenario-status";
            _scenarioStatus.style.marginTop = 6;
            section.Add(_scenarioStatus);
        }

        DemoRun _demo;
        Button _demoButton;

        void RefreshScenario()
        {
            if (_app == null || _scenarioStatus == null) return;
            RefreshTime();
            if (_igniteTool != null) _igniteTool.SetValueWithoutNotify(_app.IgniteToolOn);
            if (_app.LastError != null)
            {
                _scenarioStatus.text = _app.LastError;
                _scenarioStatus.style.color = UiStyle.Error;
                return;
            }
            _scenarioStatus.style.color = UiStyle.DimText;
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

        // ---- Display ----

        void BuildDisplaySection(VisualElement parent)
        {
            var section = UiStyle.Section("Display");
            parent.Add(section);

            section.Add(UiStyle.Text_("Overlay", 11, false, UiStyle.DimText));
            var names = new List<string> { Overlays.NoneName };
            foreach (var overlay in Overlays.Catalog)
                if (overlay.Available) names.Add(overlay.Name);
            _overlayList = new DropdownField(names, 0) { name = "overlay-list" };
            _overlayList.style.marginLeft = 0; _overlayList.style.marginRight = 0;
            _overlayList.RegisterValueChangedCallback(e =>
            {
                _app.Overlays.Select(Overlays.Find(e.newValue));
                RefreshLegend();
            });
            section.Add(_overlayList);

            var opacityLabel = UiStyle.Text_("Overlay opacity", 11, false, UiStyle.DimText);
            opacityLabel.style.marginTop = 6;
            section.Add(opacityLabel);
            _opacity = new Slider(0f, 1f) { value = Overlays.DefaultOpacity, showInputField = true, name = "overlay-opacity" };
            _opacity.style.marginLeft = 0; _opacity.style.marginRight = 0;
            _opacity.RegisterValueChangedCallback(e => _app.Overlays.SetOpacity(e.newValue));
            section.Add(_opacity);

            DisplaySlider(section, "Fuel color smoothing (m)", "fuel-color-smoothing", 0f, Overlays.MaxFuelColorSmoothingM,
                _app.Overlays.FuelColorSmoothingM, v => _app.Overlays.SetFuelColorSmoothing(v));

            // The slider has an effect when the Scenario has a Ground Image.
            DisplaySlider(section, "Ground Image opacity", "ground-image-opacity", 0f, 1f,
                _app.Overlays.GroundImageOpacity, v => _app.Overlays.SetGroundImageOpacity(v));

            _legend = new VisualElement { name = "overlay-legend" };
            _legend.style.marginTop = 8;
            section.Add(_legend);

            BuildWindDisplay(section);

            var cameraLabel = UiStyle.Text_("Camera", 11, false, UiStyle.DimText);
            cameraLabel.style.marginTop = 10;
            section.Add(cameraLabel);
            for (int r = 0; r < 2; r++)
            {
                var row = UiStyle.Row();
                for (int c = 0; c < 2; c++)
                {
                    int index = r * 2 + c;
                    row.Add(UiStyle.Button(CameraRig.PresetNames[index], () => { if (_camera != null) _camera.ApplyPreset(index); }));
                }
                section.Add(row);
            }
            var modeRow = UiStyle.Row();
            _modeButton = UiStyle.Button("", () => { if (_camera != null) _camera.ToggleMode(); });
            modeRow.Add(_modeButton);
            section.Add(modeRow);
            var help = UiStyle.Text_(
                "Orbit: right drag rotates, middle drag pans, wheel zooms. " +
                "Fly: right drag looks, W A S D Q E move, Shift is fast. Tab switches the mode. Keys 1 to 4 select a preset.",
                10, false, UiStyle.DimText);
            help.style.marginTop = 4;
            section.Add(help);
        }

        // ---- Wind Particles and slice plane ----

        Label _particleCountLabel, _sliceRange;

        Toggle DisplayToggle(VisualElement parent, string label, bool value, System.Action<bool> onChange)
        {
            var toggle = new Toggle(label) { value = value };
            toggle.style.marginLeft = 0;
            toggle.style.marginTop = 6;
            toggle.labelElement.style.color = UiStyle.Text;
            toggle.labelElement.style.fontSize = 12;
            toggle.RegisterValueChangedCallback(e => onChange(e.newValue));
            parent.Add(toggle);
            return toggle;
        }

        Slider DisplaySlider(VisualElement parent, string label, string name, float low, float high, float value, System.Action<float> onChange)
        {
            parent.Add(UiStyle.Text_(label, 11, false, UiStyle.DimText));
            var slider = new Slider(low, high) { value = value, showInputField = true, name = name };
            slider.style.marginLeft = 0; slider.style.marginRight = 0;
            slider.RegisterValueChangedCallback(e => onChange(e.newValue));
            parent.Add(slider);
            return slider;
        }

        void BuildWindDisplay(VisualElement section)
        {
            var particles = FindFirstObjectByType<WindParticles>();
            var slice = FindFirstObjectByType<SlicePlane>();

            DisplayToggle(section, "Smoke", _app.SmokeVisible, on => _app.SmokeVisible = on).name = "smoke";
            DisplaySlider(section, "Smoke density", "smoke-density", 0f, FireCAApp.MaxSmokeDensity, _app.SmokeDensity, v => _app.SmokeDensity = v);
            DisplaySlider(section, "Smoke darkness", "smoke-darkness", 0f, 1f, _app.SmokeDarkness, v => _app.SmokeDarkness = v);
            DisplaySlider(section, "Smoke billows", "smoke-billows", 0f, 1f, _app.SmokeBillows, v => _app.SmokeBillows = v);
            DisplaySlider(section, "Turbulence in smoke and particles", "turbulence-gain", 0f, FireCAApp.MaxTurbulenceGain, _app.TurbulenceGain, v => _app.TurbulenceGain = v);

            if (particles != null)
            {
                DisplayToggle(section, "Wind Particles", particles.Visible, on => particles.Visible = on).name = "wind-particles";
                _particleCountLabel = UiStyle.Text_($"Particle count: {particles.Count:N0}", 11, false, UiStyle.DimText);
                section.Add(_particleCountLabel);
                var count = new Slider(WindParticles.MinCount, WindParticles.MaxCount) { value = particles.Count, name = "wind-particle-count" };
                count.style.marginLeft = 0; count.style.marginRight = 0;
                count.RegisterValueChangedCallback(e =>
                {
                    particles.SetCount(Mathf.RoundToInt(e.newValue / 100000f) * 100000);
                    _particleCountLabel.text = $"Particle count: {particles.Count:N0}";
                });
                section.Add(count);
                DisplaySlider(section, "Particle size (m)", "wind-particle-size", 0f, WindParticles.MaxSizeM, particles.SizeM, v => particles.SetSize(v));
            }

            if (slice != null)
            {
                DisplayToggle(section, "Slice plane", slice.Visible, on => slice.SetVisible(on)).name = "slice-plane";
                var field = new DropdownField(new List<string>(SlicePlane.FieldNames), (int)slice.SelectedField) { name = "slice-field" };
                field.style.marginLeft = 0; field.style.marginRight = 0;
                field.RegisterValueChangedCallback(e =>
                {
                    slice.SetField((SlicePlane.Field)field.index);
                    _sliceRange.text = "Range: " + SlicePlane.FieldRanges[field.index] + ".";
                });
                section.Add(field);
                _sliceRange = UiStyle.Text_("Range: " + SlicePlane.FieldRanges[(int)slice.SelectedField] + ".", 10, false, UiStyle.DimText);
                section.Add(_sliceRange);
                var cut = new DropdownField(new List<string> { "East-west cut", "North-south cut" }, slice.EastWestCut ? 0 : 1) { name = "slice-cut" };
                cut.style.marginLeft = 0; cut.style.marginRight = 0;
                cut.RegisterValueChangedCallback(e => slice.SetEastWestCut(cut.index == 0));
                section.Add(cut);
                var position = new Slider(0f, 1f) { value = slice.Position01, name = "slice-position" };
                position.style.marginLeft = 0; position.style.marginRight = 0;
                position.RegisterValueChangedCallback(e => slice.SetPosition(e.newValue));
                section.Add(position);
            }
        }

        void RefreshMode()
        {
            if (_modeButton == null) return;
            var mode = _camera != null ? _camera.CurrentMode : CameraRig.Mode.Orbit;
            _modeButton.text = mode == CameraRig.Mode.Orbit ? "Camera mode: Orbit" : "Camera mode: Fly";
        }

        void RefreshLegend()
        {
            if (_legend == null || _app == null) return;
            _legend.Clear();
            OverlayInfo overlay = _app.Overlays.Selected;
            if (overlay == null) return;

            _legend.Add(UiStyle.Text_(overlay.Name, 12, true));
            string rangeText = overlay.RangeText;
            if (overlay.Name == "Air temperature" && _app.Weather != null)
            {
                // The range of the Air temperature Overlay follows the weather inputs.
                Vector2 range = Overlays.AirTemperatureRange(_app.Weather);
                rangeText = $"{range.x:F0} to {range.y:F0}";
            }
            _legend.Add(UiStyle.Text_($"Unit: {overlay.Unit}. Range: {rangeText}.", 11, false, UiStyle.DimText));

            if (overlay.Categorical)
            {
                for (int code = 1; code <= FuelCodes.FuelModelCount; code++)
                    _legend.Add(Swatch(Overlays.CategoricalColor(code), $"{code}  {FuelCodes.NameOf(code)}"));
                _legend.Add(Swatch(Overlays.CategoricalNotBurnable, "Not burnable"));
            }
            else
            {
                var bar = new Image { image = _app.Overlays.ColorMap, scaleMode = ScaleMode.StretchToFill };
                bar.style.height = 12;
                bar.style.marginTop = 4;
                _legend.Add(bar);
                var ends = UiStyle.Row();
                ends.style.justifyContent = Justify.SpaceBetween;
                string[] range = rangeText.Split(new[] { " to " }, System.StringSplitOptions.None);
                // The color map of the Arrival time Overlay spans 6 simulated hours, then repeats.
                ends.Add(UiStyle.Text_(range.Length == 2 ? range[0] : "0 h", 10, false, UiStyle.DimText));
                ends.Add(UiStyle.Text_(range.Length == 2 ? range[1].Split(',')[0] : "6 h", 10, false, UiStyle.DimText));
                _legend.Add(ends);
            }
        }

        static VisualElement Swatch(Color color, string text)
        {
            var row = UiStyle.Row();
            row.style.marginTop = 2;
            var box = new VisualElement();
            box.style.width = 12; box.style.height = 12;
            box.style.marginRight = 6;
            box.style.backgroundColor = color;
            row.Add(box);
            row.Add(UiStyle.Text_(text, 11));
            return row;
        }
    }
}
