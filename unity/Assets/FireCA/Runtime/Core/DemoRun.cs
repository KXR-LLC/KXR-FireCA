using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace FireCA
{
    /// <summary>
    /// The scripted demo run of docs/05-milestones.md. The user starts the run with one key (F5).
    /// The run has four beats. Each beat sets the camera preset, the time scale, and the Overlay.
    /// </summary>
    public sealed class DemoRun : MonoBehaviour
    {
        public readonly struct Beat
        {
            public readonly string Name;
            /// <summary>The Sim Clock value at which the beat ends, s. Beat 1 runs with the Sim Clock paused.</summary>
            public readonly double EndSimSeconds;
            public readonly float TimeScale;
            public readonly int CameraPreset;
            public readonly string Overlay;
            public readonly string Caption;
            /// <summary>False when the Wind Particles and the smoke would hide the Overlay of the beat.</summary>
            public readonly bool ShowWindAndSmoke;

            public Beat(string name, double endSimSeconds, float timeScale, int cameraPreset, string overlay, string caption,
                bool showWindAndSmoke = true)
            {
                Name = name; EndSimSeconds = endSimSeconds; TimeScale = timeScale;
                CameraPreset = cameraPreset; Overlay = overlay; Caption = caption;
                ShowWindAndSmoke = showWindAndSmoke;
            }
        }

        // The Scenario starts at 13:00 solar time. The beat ends are 13:00, 13:40, 14:30, and 17:00.
        public static readonly Beat[] Beats =
        {
            new Beat("Beat 1", 0.0, 0f, 0, Overlays.NoneName,
                "13:00, paused. The canyon, the sun shadows, and the Wind Particles in the canyon."),
            new Beat("Beat 2", 40.0 * 60.0, 60f, 1, Overlays.NoneName,
                "13:00 to 13:40 at 60x. Ignition on the canyon floor. The fire follows the channeled wind."),
            new Beat("Beat 3", 90.0 * 60.0, 60f, 2, Overlays.NoneName,
                "13:40 to 14:30 at 60x. The fire climbs the chute, and the plume attaches to the slope."),
            new Beat("Beat 4", 240.0 * 60.0, 240f, 3, "Arrival time",
                "14:30 to 17:00 at 240x. The whole burn in bands of 10 minutes, and the Stats HUD numbers.", false),
        };

        public enum State { Idle, SpinUp, Running, Done }

        /// <summary>The real time of beat 1, s.</summary>
        public float IntroSeconds = 14f;
        /// <summary>The speed of the slow orbit of beat 1, degrees per second.</summary>
        public float OrbitDegPerSecond = 3f;

        public State CurrentState { get; private set; } = State.Idle;
        /// <summary>The index of the running beat, from 0.</summary>
        public int BeatIndex { get; private set; }
        public bool Running => CurrentState == State.SpinUp || CurrentState == State.Running;

        FireCAApp _app;
        CameraRig _camera;
        WindParticles _particles;
        Label _caption;
        float _introTimer;

        void Start()
        {
            _app = FireCAApp.Instance;
            _camera = FindFirstObjectByType<CameraRig>();
            _particles = FindFirstObjectByType<WindParticles>();
            var document = FindFirstObjectByType<UIDocument>();
            if (document != null && document.rootVisualElement != null)
            {
                _caption = new Label { name = "demo-caption", pickingMode = PickingMode.Ignore };
                _caption.style.position = Position.Absolute;
                _caption.style.bottom = 24; _caption.style.left = 340; _caption.style.right = 40;
                _caption.style.fontSize = 18;
                _caption.style.color = Color.white;
                _caption.style.unityTextAlign = TextAnchor.MiddleCenter;
                _caption.style.unityFontStyleAndWeight = FontStyle.Bold;
                _caption.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
                _caption.style.paddingTop = 8; _caption.style.paddingBottom = 8;
                _caption.style.display = DisplayStyle.None;
                document.rootVisualElement.Add(_caption);
            }
        }

        void Update()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f5Key.wasPressedThisFrame)
            {
                if (Running) StopDemo(); else StartDemo();
            }
            if (_app == null || _app.Scenario == null || _app.Scheduler == null) return;

            switch (CurrentState)
            {
                case State.SpinUp:
                    SetCaption($"Wind spin-up: {_app.Wind.SpinUpProgress * 100f:F0} percent");
                    if (!_app.Scheduler.SpinningUp) EnterBeat(0);
                    break;
                case State.Running:
                    UpdateBeat();
                    break;
            }
        }

        /// <summary>Starts the demo run: reloads the Scenario, waits for the spin-up, then runs the four beats.</summary>
        public void StartDemo()
        {
            if (_app == null || _app.Scenario == null) return;
            _app.IgniteToolOn = false;
            _app.CouplingGain = 1f;
            _app.SmokeVisible = true;
            if (_particles != null) _particles.Visible = true;
            _app.ResetScenario();
            _app.Clock.Playing = false;
            CurrentState = State.SpinUp;
            BeatIndex = 0;
            if (_camera != null) { _camera.SetAutoOrbit(false); _camera.SetMode(CameraRig.Mode.Orbit); _camera.ApplyPreset(0); }
        }

        /// <summary>Stops the demo run and pauses the Sim Clock.</summary>
        public void StopDemo()
        {
            if (_app != null)
            {
                _app.Clock.Playing = false;
                _app.SmokeVisible = true;
            }
            if (_particles != null) _particles.Visible = true;
            CurrentState = State.Idle;
            SetCaption(null);
        }

        void EnterBeat(int index)
        {
            BeatIndex = index;
            CurrentState = State.Running;
            Beat beat = Beats[index];
            if (_camera != null) { _camera.SetMode(CameraRig.Mode.Orbit); _camera.ApplyPreset(beat.CameraPreset); }
            _app.Overlays.Select(Overlays.Find(beat.Overlay));
            _app.SmokeVisible = beat.ShowWindAndSmoke;
            if (_particles != null) _particles.Visible = beat.ShowWindAndSmoke;
            if (beat.TimeScale > 0f)
            {
                _app.Clock.SetTimeScale(beat.TimeScale);
                _app.Clock.Playing = true;
            }
            else
            {
                _app.Clock.Playing = false;
                _introTimer = 0f;
            }
            SetCaption($"{beat.Name}. {beat.Caption}");
        }

        void UpdateBeat()
        {
            Beat beat = Beats[BeatIndex];
            bool finished;
            if (beat.TimeScale <= 0f)
            {
                // Beat 1: the Sim Clock is paused, and the camera orbits slowly.
                _introTimer += Time.unscaledDeltaTime;
                if (_camera != null) _camera.Orbit(OrbitDegPerSecond * Time.unscaledDeltaTime);
                finished = _introTimer >= IntroSeconds;
            }
            else
            {
                finished = _app.Clock.Seconds >= beat.EndSimSeconds;
            }
            if (!finished) return;
            if (BeatIndex + 1 < Beats.Length)
            {
                EnterBeat(BeatIndex + 1);
            }
            else
            {
                _app.Clock.Playing = false;
                CurrentState = State.Done;
                SetCaption("The demo run is complete. Press F5 to run the demo again.");
            }
        }

        void SetCaption(string text)
        {
            if (_caption == null) return;
            _caption.text = text ?? "";
            _caption.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }
    }
}
