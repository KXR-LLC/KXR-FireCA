using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FireCA
{
    /// <summary>
    /// Measures the performance targets of docs/05-milestones.md. The benchmark runs when the player starts with
    /// "-firecaBench &lt;report path&gt;". The benchmark writes a JSON report and quits.
    /// </summary>
    public sealed class FrameBenchmark : MonoBehaviour
    {
        const float SettleSeconds = 2f;
        const float PresetSeconds = 3f;
        const float RunSeconds = 10f;
        /// <summary>The fire burns to this Sim Clock value before the frame rate measurements, s.</summary>
        const double WarmUpSimSeconds = 45.0 * 60.0;

        FireCAApp _app;
        CameraRig _rig;
        readonly StringBuilder _json = new StringBuilder();

        IEnumerator Start()
        {
            string reportPath = CommandLine.Value("-firecaBench");
            if (string.IsNullOrEmpty(reportPath)) yield break;

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            _app = FireCAApp.Instance;
            while (_app.Scenario == null && _app.LastError == null) yield return null;
            _rig = FindFirstObjectByType<CameraRig>();
            yield return null;
            if (_app.Scenario == null || _rig == null)
            {
                File.WriteAllText(reportPath, "{ \"loadError\": \"" + (_app.LastError ?? "no camera") + "\" }\n");
                Application.Quit();
                yield break;
            }

            _json.Append("{\n");
            Field("scenario", _app.Scenario.Id);
            Field("loadSeconds", _app.LastLoadSeconds);
            Field("width", Screen.width);
            Field("height", Screen.height);
            Field("fullScreenMode", Screen.fullScreenMode.ToString());
            Field("graphicsDevice", SystemInfo.graphicsDeviceName);
            Field("graphicsApi", SystemInfo.graphicsDeviceType.ToString());
            Field("textureMemoryMB", _app.Registry.TotalBytes / (1024f * 1024f));

            // Spin-up.
            float spinStart = Time.realtimeSinceStartup;
            while (_app.Scheduler.SpinningUp) yield return null;
            Field("spinUpSeconds", Time.realtimeSinceStartup - spinStart);

            // Frame rate from every camera preset, with the Sim Clock paused.
            _json.Append("  \"presetsPaused\": [\n");
            for (int i = 0; i < _rig.Presets.Count; i++)
            {
                _rig.ApplyPreset(i);
                yield return Wait(SettleSeconds);
                var m = new Measurement();
                yield return Measure(PresetSeconds, m);
                _json.Append($"    {{ \"name\": \"{_rig.Presets[i].Name}\", {m.Json} }}{(i < _rig.Presets.Count - 1 ? "," : "")}\n");
            }
            _json.Append("  ],\n");

            // Burn to a state with fire in the chute, smoke, and many active blocks.
            _app.CouplingGain = 1f;
            _app.Clock.SetTimeScale(240f);
            _app.Clock.Playing = true;
            _rig.ApplyPreset(0);
            float warmStart = Time.realtimeSinceStartup;
            while (_app.Clock.Seconds < WarmUpSimSeconds && Time.realtimeSinceStartup - warmStart < 120f) yield return null;
            Field("warmUpSimMinutes", (float)(_app.Clock.Seconds / 60.0));
            Field("warmUpRealSeconds", Time.realtimeSinceStartup - warmStart);

            // Frame rate at 60x and at 240x, with fire coupling on.
            yield return TimeScaleRun("run60x", 60f, 2);
            yield return TimeScaleRun("run240x", 240f, 0);

            // Smoke Pass: the frame time with the smoke on, minus the frame time with the smoke off.
            _app.Clock.Playing = false;
            _rig.ApplyPreset(2);
            yield return Wait(SettleSeconds);
            var smokeOn = new Measurement();
            yield return Measure(PresetSeconds, smokeOn);
            _app.SmokeVisible = false;
            yield return Wait(1f);
            var smokeOff = new Measurement();
            yield return Measure(PresetSeconds, smokeOff);
            _app.SmokeVisible = true;
            Field("smokePassMs", 1000f / smokeOn.MeanFps - 1000f / smokeOff.MeanFps);
            Field("smokeOnFps", smokeOn.MeanFps);
            Field("smokeOffFps", smokeOff.MeanFps);
            Field("burnedAreaHa", (float)_app.Spread.BurnedAreaHa);
            Field("activeBlocks", _app.Spread.ActiveBlocks);

            // Compute passes, timed with the GPU idle between the passes.
            _app.SchedulerEnabled = false;
            yield return null;
            WindSolver wind = _app.Wind;
            SpreadSolver spread = _app.Spread;
            Field("pressureIterations", wind.PressureIterations);
            Field("windStepMs", TimeGpu(() => wind.Step(), 40));
            Field("behaviorPassFullGridMs", TimeGpu(() => spread.RunBehavior(true), 40));
            Field("arrivalPassAllBlocksMs", TimeGpu(() => spread.BenchmarkArrivalAllBlocks(), 40));
            Field("windStepBudget", _app.Scheduler.WindStepBudget);
            Field("fireStepBudget", _app.Scheduler.FireStepBudget, last: true);
            _json.Append("}\n");

            File.WriteAllText(reportPath, _json.ToString());
            Debug.Log($"[FireCA] Wrote the benchmark report to {reportPath}.");
            Application.Quit();
        }

        // The mean GPU time of an action, ms. A statistics readback waits for the GPU before and after the actions.
        float TimeGpu(Action action, int count)
        {
            _app.Wind.ReadStats();
            var baseline = Stopwatch.StartNew();
            _app.Wind.ReadStats();
            double readbackMs = baseline.Elapsed.TotalMilliseconds;
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < count; i++) action();
            _app.Wind.ReadStats();
            return (float)((watch.Elapsed.TotalMilliseconds - readbackMs) / count);
        }

        IEnumerator TimeScaleRun(string name, float timeScale, int preset)
        {
            _app.Clock.SetTimeScale(timeScale);
            _app.Clock.Playing = true;
            _rig.ApplyPreset(preset);
            yield return Wait(SettleSeconds);
            double simStart = _app.Clock.Seconds;
            long windStart = _app.Wind.StepCount, fireStart = _app.Spread.StepCount;
            var m = new Measurement();
            yield return Measure(RunSeconds, m);
            float achieved = (float)((_app.Clock.Seconds - simStart) / m.Seconds);
            _json.Append($"  \"{name}\": {{ \"requestedTimeScale\": {F(timeScale)}, \"achievedTimeScale\": {F(achieved)}, {m.Json}, " +
                $"\"windStepsPerSecond\": {F((_app.Wind.StepCount - windStart) / m.Seconds)}, " +
                $"\"fireStepsPerSecond\": {F((_app.Spread.StepCount - fireStart) / m.Seconds)}, " +
                $"\"cellUpdatesBillionPerSecond\": {F((float)(_app.Scheduler.CellUpdatesPerSecond / 1.0e9))}, " +
                $"\"camera\": \"{_rig.Presets[preset].Name}\" }},\n");
        }

        sealed class Measurement
        {
            public float MeanFps, OnePercentLowFps, MinFps, Seconds;
            public int Frames;
            public string Json =>
                $"\"meanFps\": {F(MeanFps)}, \"onePercentLowFps\": {F(OnePercentLowFps)}, \"minFps\": {F(MinFps)}, \"frames\": {Frames}";
        }

        static IEnumerator Wait(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until) yield return null;
        }

        static IEnumerator Measure(float seconds, Measurement m)
        {
            var frames = new List<float>(8192);
            float start = Time.realtimeSinceStartup, last = start;
            while (last - start < seconds)
            {
                yield return null;
                float now = Time.realtimeSinceStartup;
                frames.Add(now - last);
                last = now;
            }
            frames.Sort();
            float total = 0f;
            foreach (float f in frames) total += f;
            m.Frames = frames.Count;
            m.Seconds = total;
            m.MeanFps = frames.Count / total;
            m.MinFps = 1f / frames[frames.Count - 1];
            m.OnePercentLowFps = 1f / frames[Mathf.Min(frames.Count - 1, (int)(frames.Count * 0.99f))];
        }

        void Field(string name, string value, bool last = false) => _json.Append($"  \"{name}\": \"{value}\"{(last ? "" : ",")}\n");
        void Field(string name, float value, bool last = false) => _json.Append($"  \"{name}\": {F(value)}{(last ? "" : ",")}\n");
        void Field(string name, int value, bool last = false) => _json.Append($"  \"{name}\": {value}{(last ? "" : ",")}\n");

        static string F(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
    }
}
