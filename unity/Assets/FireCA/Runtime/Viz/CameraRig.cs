using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace FireCA
{
    /// <summary>The camera. Provides an orbit mode, a fly mode, and four preset views. Keeps the camera above the terrain.</summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CameraRig : MonoBehaviour
    {
        public enum Mode { Orbit, Fly }

        public readonly struct Preset
        {
            public readonly string Name;
            public readonly Vector3 Position;
            public readonly Vector3 LookAt;
            public Preset(string name, Vector3 position, Vector3 lookAt) { Name = name; Position = position; LookAt = lookAt; }
        }

        public static readonly string[] PresetNames = { "Overview", "Canyon Mouth", "Chute", "Top Down" };

        const float MinClearanceM = 3f;
        const float MinPitchDeg = -20f;
        const float MaxPitchDeg = 89.5f;

        public Mode CurrentMode { get; private set; } = Mode.Orbit;
        public IReadOnlyList<Preset> Presets => _presets;
        /// <summary>Returns true when the pointer is over the UI. The camera then ignores a new drag.</summary>
        public Func<Vector2, bool> PointerBlocked;
        public event Action ModeChanged;

        // ---- orbit tool (docs/decisions.md, decision D35) ----

        public const float MinOrbitPeriodHours = 0.25f;
        public const float MaxOrbitPeriodHours = 24f;
        public const float DefaultOrbitPeriodHours = 6f;

        /// <summary>True while the orbit tool turns the camera around its look-at point.</summary>
        public bool AutoOrbit { get; private set; }
        /// <summary>The time of one full orbit, in hours of the Sim Clock.</summary>
        public float OrbitPeriodHours
        {
            get => _orbitPeriodHours;
            set => _orbitPeriodHours = Mathf.Clamp(value, MinOrbitPeriodHours, MaxOrbitPeriodHours);
        }
        public event Action AutoOrbitChanged;

        float _orbitPeriodHours = DefaultOrbitPeriodHours;
        double _orbitClockS;

        /// <summary>
        /// Starts or stops the orbit tool. The orbit starts at the current position of the camera and goes around
        /// the look-at point of the camera. In the fly mode, the look-at point is the terrain point at the center
        /// of the view.
        /// </summary>
        public void SetAutoOrbit(bool on)
        {
            if (on == AutoOrbit) return;
            if (on && CurrentMode == Mode.Fly)
            {
                if (_app != null && _app.RaycastTerrain(new Ray(transform.position, Forward()), out Vector3 hit))
                    _distance = Mathf.Max(Vector3.Distance(transform.position, hit), 1f);
                _target = transform.position + Forward() * _distance;
                CurrentMode = Mode.Orbit;
                ModeChanged?.Invoke();
            }
            AutoOrbit = on;
            if (_app != null) _orbitClockS = _app.Clock.Seconds;
            AutoOrbitChanged?.Invoke();
        }

        // Turns the camera by the share of one orbit that the Sim Clock moved since the last frame.
        void AdvanceAutoOrbit()
        {
            double now = _app.Clock.Seconds;
            double elapsed = now - _orbitClockS;
            _orbitClockS = now;
            // A Scenario load puts the Sim Clock back to zero.
            if (elapsed <= 0.0) return;
            _yawDeg = Mathf.Repeat(_yawDeg + (float)(elapsed / (_orbitPeriodHours * 3600.0) * 360.0), 360f);
        }

        readonly List<Preset> _presets = new List<Preset>();
        FireCAApp _app;
        Camera _camera;
        Vector3 _target;
        /// <summary>The point that the camera looks at and orbits.</summary>
        public Vector3 Target => _target;
        float _yawDeg, _pitchDeg = 35f, _distance = 3000f;
        float _flySpeed = 250f;
        bool _dragging;

        void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        void Start()
        {
            _app = FireCAApp.Instance;
            if (_app != null)
            {
                _app.ScenarioChanged += OnScenarioChanged;
                if (_app.Scenario != null) OnScenarioChanged();
            }
        }

        void OnDestroy()
        {
            if (_app != null) _app.ScenarioChanged -= OnScenarioChanged;
        }

        void OnScenarioChanged()
        {
            if (_app.Scenario == null || _app.LastError != null) return;
            BuildPresets(_app.Scenario);
            float extent = _app.Scenario.Grid.ExtentM;
            _camera.nearClipPlane = Mathf.Max(0.5f, extent / 8192f);
            _camera.farClipPlane = extent * 8f;
            _flySpeed = extent / 32f;
            ApplyPreset(0);
        }

        public void ToggleMode() => SetMode(CurrentMode == Mode.Orbit ? Mode.Fly : Mode.Orbit);

        public void SetMode(Mode mode)
        {
            if (mode == CurrentMode) return;
            if (mode == Mode.Fly) SetAutoOrbit(false);
            if (mode == Mode.Orbit)
            {
                // Put the orbit target on the view ray, at the current orbit distance.
                _target = transform.position + Forward() * _distance;
            }
            CurrentMode = mode;
            ModeChanged?.Invoke();
        }

        public void ApplyPreset(int index)
        {
            if (index < 0 || index >= _presets.Count) return;
            LookFrom(_presets[index]);
        }

        /// <summary>Puts the camera at a position, with a look at a point.</summary>
        public void LookFrom(Vector3 position, Vector3 lookAt) => LookFrom(new Preset("", position, lookAt));

        void LookFrom(Preset p)
        {
            Vector3 dir = p.LookAt - p.Position;
            _distance = Mathf.Max(dir.magnitude, 1f);
            dir /= _distance;
            _pitchDeg = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(-dir.y, -1f, 1f)) * Mathf.Rad2Deg, MinPitchDeg, MaxPitchDeg);
            float horizontal = new Vector2(dir.x, dir.z).magnitude;
            _yawDeg = horizontal > 1e-4f ? Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg : 0f;
            _target = p.LookAt;
            transform.position = p.Position;
            transform.rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
            if (CurrentMode == Mode.Orbit) PlaceOrbit();
            KeepAboveTerrain();
        }

        /// <summary>Turns the orbit camera around the target. The demo run uses this for the slow orbit.</summary>
        public void Orbit(float yawDeltaDeg)
        {
            _yawDeg += yawDeltaDeg;
            if (CurrentMode == Mode.Orbit) PlaceOrbit();
        }

        Vector3 Forward() => Quaternion.Euler(_pitchDeg, _yawDeg, 0f) * Vector3.forward;

        void Update()
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (_app == null || _app.Scenario == null) return;
            if (AutoOrbit) AdvanceAutoOrbit();
            if (mouse == null || keyboard == null)
            {
                if (AutoOrbit) { PlaceOrbit(); KeepAboveTerrain(); }
                return;
            }

            if (keyboard.tabKey.wasPressedThisFrame) ToggleMode();
            if (keyboard.digit1Key.wasPressedThisFrame) ApplyPreset(0);
            if (keyboard.digit2Key.wasPressedThisFrame) ApplyPreset(1);
            if (keyboard.digit3Key.wasPressedThisFrame) ApplyPreset(2);
            if (keyboard.digit4Key.wasPressedThisFrame) ApplyPreset(3);

            Vector2 pointer = mouse.position.ReadValue();
            bool blocked = PointerBlocked != null && PointerBlocked(pointer);
            bool rightDown = mouse.rightButton.isPressed, middleDown = mouse.middleButton.isPressed;
            if ((mouse.rightButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame) && !blocked) _dragging = true;
            if (!rightDown && !middleDown) _dragging = false;

            Vector2 delta = _dragging ? mouse.delta.ReadValue() : Vector2.zero;
            // One wheel notch reports 120 on Windows and 1 on some other platforms.
            float rawScroll = mouse.scroll.ReadValue().y;
            float scroll = blocked || rawScroll == 0f
                ? 0f
                : Mathf.Sign(rawScroll) * Mathf.Clamp(Mathf.Abs(rawScroll) / 120f, 1f, 3f);

            if (_dragging && rightDown)
            {
                _yawDeg += delta.x * 0.2f;
                _pitchDeg = Mathf.Clamp(_pitchDeg - delta.y * 0.2f, MinPitchDeg, MaxPitchDeg);
            }

            if (CurrentMode == Mode.Orbit) UpdateOrbit(delta, scroll, middleDown);
            else UpdateFly(keyboard, scroll);

            KeepAboveTerrain();
        }

        void UpdateOrbit(Vector2 delta, float scroll, bool middleDown)
        {
            float extent = _app.Scenario.Grid.ExtentM;
            if (scroll != 0f) _distance = Mathf.Clamp(_distance * Mathf.Exp(-scroll * 0.15f), 20f, extent * 3f);
            if (_dragging && middleDown)
            {
                Quaternion yaw = Quaternion.Euler(0f, _yawDeg, 0f);
                float scale = _distance * 0.0012f;
                _target -= yaw * new Vector3(delta.x, 0f, delta.y) * scale;
                _target.x = Mathf.Clamp(_target.x, 0f, extent);
                _target.z = Mathf.Clamp(_target.z, 0f, extent);
                _target.y = _app.SampleElevation(_target.x, _target.z);
            }
            PlaceOrbit();
        }

        void PlaceOrbit()
        {
            Quaternion rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
            transform.SetPositionAndRotation(_target - rotation * Vector3.forward * _distance, rotation);
        }

        void UpdateFly(Keyboard keyboard, float scroll)
        {
            if (scroll != 0f) _flySpeed = Mathf.Clamp(_flySpeed * Mathf.Exp(scroll * 0.2f), 5f, 5000f);
            Vector3 move = Vector3.zero;
            if (keyboard.wKey.isPressed) move.z += 1f;
            if (keyboard.sKey.isPressed) move.z -= 1f;
            if (keyboard.dKey.isPressed) move.x += 1f;
            if (keyboard.aKey.isPressed) move.x -= 1f;
            if (keyboard.eKey.isPressed) move.y += 1f;
            if (keyboard.qKey.isPressed) move.y -= 1f;
            float speed = _flySpeed * (keyboard.leftShiftKey.isPressed ? 4f : 1f);
            Quaternion rotation = Quaternion.Euler(_pitchDeg, _yawDeg, 0f);
            Vector3 position = transform.position + rotation * new Vector3(move.x, 0f, move.z) * (speed * Time.unscaledDeltaTime)
                + Vector3.up * (move.y * speed * Time.unscaledDeltaTime);
            float extent = _app.Scenario.Grid.ExtentM;
            position.x = Mathf.Clamp(position.x, -extent, 2f * extent);
            position.z = Mathf.Clamp(position.z, -extent, 2f * extent);
            transform.SetPositionAndRotation(position, rotation);
        }

        void KeepAboveTerrain()
        {
            Vector3 p = transform.position;
            float extent = _app != null && _app.Scenario != null ? _app.Scenario.Grid.ExtentM : 0f;
            if (extent <= 0f) return;
            if (p.x < 0f || p.z < 0f || p.x > extent || p.z > extent) return;
            float floor = _app.SampleElevation(p.x, p.z) + MinClearanceM;
            if (p.y < floor)
            {
                p.y = floor;
                transform.position = p;
            }
        }

        // ---- presets ----

        void BuildPresets(ScenarioFiles scenario)
        {
            _presets.Clear();
            var fromFile = scenario.Data.cameraPresets ?? Array.Empty<ScenarioData.CameraPresetData>();
            foreach (string name in PresetNames)
            {
                Preset? found = null;
                foreach (var p in fromFile)
                    if (p != null && p.name == name) found = new Preset(name, p.positionM, p.lookAtM);
                _presets.Add(found ?? FallbackPreset(name, scenario));
            }
        }

        // A preset for a Scenario that has no cameraPresets entry with the name.
        Preset FallbackPreset(string name, ScenarioFiles s)
        {
            float extent = s.Grid.ExtentM;
            float low = s.MinElevation, high = s.MaxElevation;
            Vector3 center = new Vector3(extent * 0.5f, low + (high - low) * 0.4f, extent * 0.5f);
            switch (name)
            {
                case "Top Down":
                    return new Preset(name, new Vector3(center.x, high + extent * 0.92f, center.z), new Vector3(center.x, low, center.z));
                case "Canyon Mouth":
                {
                    Vector3 mouth = LowestEdgePoint(s);
                    Vector3 inward = (new Vector3(center.x, mouth.y, center.z) - mouth).normalized;
                    Vector3 position = mouth - inward * (extent * 0.02f) + Vector3.up * (extent * 0.06f);
                    return new Preset(name, position, mouth + inward * (extent * 0.3f));
                }
                case "Chute":
                {
                    var ignitions = s.Data.ignitions;
                    if (ignitions != null && ignitions.Length > 0)
                    {
                        Vector3 at = new Vector3(ignitions[0].xM, 0f, ignitions[0].zM);
                        at.y = SampleCpu(s, at.x, at.z);
                        Vector3 position = at + new Vector3(-0.1f, 0.07f, -0.1f) * extent;
                        return new Preset(name, position, at);
                    }
                    goto default;
                }
                default:
                    return new Preset(name, new Vector3(extent * 0.95f, low + extent * 0.5f, -extent * 0.2f), center);
            }
        }

        static float SampleCpu(ScenarioFiles s, float x, float z)
        {
            int n = s.Grid.FireCells;
            int i = Mathf.Clamp((int)(x / GridSpec.FireCellSizeM), 0, n - 1);
            int j = Mathf.Clamp((int)(z / GridSpec.FireCellSizeM), 0, n - 1);
            return s.Elevation[j * n + i];
        }

        static Vector3 LowestEdgePoint(ScenarioFiles s)
        {
            int n = s.Grid.FireCells;
            float best = float.PositiveInfinity;
            int bi = 0, bj = 0;
            for (int k = 0; k < n; k += 4)
            {
                Check(k, 0); Check(k, n - 1); Check(0, k); Check(n - 1, k);
            }
            return new Vector3((bi + 0.5f) * GridSpec.FireCellSizeM, best, (bj + 0.5f) * GridSpec.FireCellSizeM);

            void Check(int i, int j)
            {
                float e = s.Elevation[j * n + i];
                if (e < best) { best = e; bi = i; bj = j; }
            }
        }
    }
}
