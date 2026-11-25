using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;

namespace FireCA
{
    /// <summary>
    /// The Step Scheduler: the CPU component that decides how many solver steps run in each frame.
    /// The order of the steps follows docs/00-overview.md, section "Frame Loop". M1 has no Wind Solver steps.
    /// </summary>
    public sealed class StepScheduler
    {
        /// <summary>The behavior pass runs on the full Fire Grid at least once per second of real time.</summary>
        public const float FullBehaviorIntervalS = 1f;

        readonly SimClock _clock;
        readonly WeatherModel _weather;
        readonly WindSolver _wind;
        readonly SpreadSolver _spread;
        readonly List<ScenarioData.IgnitionData> _pendingIgnitions = new List<ScenarioData.IgnitionData>();
        readonly CustomSampler _fireSampler, _windSampler;
        readonly Recorder _fireRecorder, _windRecorder;
        int _windStepsLastFrame;
        long _windRateWindowStart;
        float _sinceFullBehavior;
        int _stepsLastFrame;
        float _rateWindow;
        long _rateWindowStart;

        /// <summary>The largest count of Fire steps in one frame.</summary>
        public int FireStepBudget = 8;
        /// <summary>The largest count of Wind steps in one frame.</summary>
        public int WindStepBudget = 4;
        /// <summary>The count of spin-up Wind steps in one frame, before the Sim Clock starts.</summary>
        public int SpinUpStepsPerFrame = 6;

        public int WindStepsThisFrame { get; private set; }
        public float WindStepsPerSecond { get; private set; }
        /// <summary>GPU time of one Wind step, ms. The value is 0 when the platform gives no GPU timing.</summary>
        public float WindStepGpuMs { get; private set; }
        /// <summary>True when the last frame spent the Wind step budget.</summary>
        public bool WindBudgetSpent { get; private set; }
        /// <summary>
        /// Fire and wind cell updates per second. The value counts each Fire Grid cell once per Fire step and
        /// each Wind Grid cell once per pressure iteration.
        /// </summary>
        public double CellUpdatesPerSecond { get; private set; }

        /// <summary>True while the Wind Solver spin-up runs. The Sim Clock does not start before the spin-up ends.</summary>
        public bool SpinningUp => _wind != null && _wind.Enabled && !_wind.SpunUp;

        public int FireStepsThisFrame { get; private set; }
        public float FireStepsPerSecond { get; private set; }
        /// <summary>GPU time of one Fire step, ms. The value is 0 when the platform gives no GPU timing.</summary>
        public float FireStepGpuMs { get; private set; }
        /// <summary>True when the last frame spent the Fire step budget.</summary>
        public bool FireBudgetSpent { get; private set; }

        readonly long _gridFireCells, _gridWindCells;

        public StepScheduler(SimClock clock, WeatherModel weather, WindSolver wind, SpreadSolver spread, ScenarioData data, GridSpec grid)
        {
            _clock = clock;
            _weather = weather;
            _wind = wind;
            _gridFireCells = grid.FireCellTotal;
            _gridWindCells = grid.WindCellTotal;
            _windSampler = CustomSampler.Create("FireCA Wind Steps", true);
            _windRecorder = _windSampler.GetRecorder();
            _windRecorder.enabled = true;
            _spread = spread;
            if (data.ignitions != null) _pendingIgnitions.AddRange(data.ignitions);
            _fireSampler = CustomSampler.Create("FireCA Fire Steps", true);
            _fireRecorder = _fireSampler.GetRecorder();
            _fireRecorder.enabled = true;
        }

        /// <summary>Runs the simulation part of one frame.</summary>
        public void Tick(float frameSeconds)
        {
            // GPU time of the Fire steps of the last frame.
            if (_stepsLastFrame > 0 && _fireRecorder.isValid && _fireRecorder.gpuElapsedNanoseconds > 20000L * _stepsLastFrame)
            {
                float ms = _fireRecorder.gpuElapsedNanoseconds / 1.0e6f / _stepsLastFrame;
                FireStepGpuMs = FireStepGpuMs <= 0f ? ms : Mathf.Lerp(FireStepGpuMs, ms, 0.1f);
            }

            if (_windStepsLastFrame > 0 && _windRecorder.isValid && _windRecorder.gpuElapsedNanoseconds > 500000L * _windStepsLastFrame)
            {
                float ms = _windRecorder.gpuElapsedNanoseconds / 1.0e6f / _windStepsLastFrame;
                WindStepGpuMs = WindStepGpuMs <= 0f ? ms : Mathf.Lerp(WindStepGpuMs, ms, 0.1f);
            }

            // Spin-up: the Wind Solver runs spinUpSteps Wind steps before the Sim Clock starts.
            if (SpinningUp)
            {
                _windSampler.Begin();
                int spin = 0;
                for (; spin < SpinUpStepsPerFrame && !_wind.SpunUp; spin++) _wind.SpinUpStep();
                _windSampler.End();
                _windStepsLastFrame = spin;
                WindStepsThisFrame = spin;
                FireStepsThisFrame = 0;
                _stepsLastFrame = 0;
                if (_wind.SpunUp) _spread.RunBehavior(true);
                _clock.Advance(_clock.Seconds, frameSeconds);
                UpdateRates(frameSeconds);
                return;
            }

            // 1. Target Sim Clock advance.
            double target = _clock.Seconds + (_clock.Playing ? frameSeconds * (double)_clock.TimeScale : 0.0);

            // 2. Wind steps, until the wind time reaches the target or the budget is spent.
            int windSteps = 0;
            if (_wind != null && _wind.Enabled)
            {
                _windSampler.Begin();
                while (_wind.WindTime < target && windSteps < WindStepBudget)
                {
                    _wind.Step();
                    windSteps++;
                }
                _windSampler.End();
                // 3. If the budget is spent, the Sim Clock stops at the wind time.
                WindBudgetSpent = _wind.WindTime < target;
                if (WindBudgetSpent) target = _wind.WindTime;
            }
            WindStepsThisFrame = windSteps;
            _windStepsLastFrame = windSteps;

            // 4. Weather Model update, once per 60 simulated seconds.
            bool weatherChanged = _weather.Update(_clock.Seconds);

            // Scenario ignitions that are due.
            for (int i = _pendingIgnitions.Count - 1; i >= 0; i--)
            {
                var ignition = _pendingIgnitions[i];
                double time = ignition.hour * 3600.0;
                if (time > _clock.Seconds) continue;
                _spread.Ignite(new Vector2(ignition.xM, ignition.zM), ignition.radiusM, time);
                _pendingIgnitions.RemoveAt(i);
            }

            _sinceFullBehavior += frameSeconds;
            if (weatherChanged || _sinceFullBehavior >= FullBehaviorIntervalS)
            {
                _spread.RunBehavior(true);
                _sinceFullBehavior = 0f;
            }

            // 5. Fire steps, until the fire time reaches the target or the budget is spent.
            int steps = 0;
            _fireSampler.Begin();
            while (_spread.FireTime < target && steps < FireStepBudget)
            {
                if (!_spread.Step())
                {
                    _spread.SetFireTime(target);
                    break;
                }
                steps++;
            }
            _fireSampler.End();

            // 6. If the budget is spent, the Sim Clock stops at the fire time.
            FireBudgetSpent = _spread.FireTime < target;
            if (FireBudgetSpent) target = _spread.FireTime;

            // 7. Set the Sim Clock.
            _clock.Advance(target, frameSeconds);

            FireStepsThisFrame = steps;
            _stepsLastFrame = steps;
            UpdateRates(frameSeconds);
        }

        void UpdateRates(float frameSeconds)
        {
            _rateWindow += frameSeconds;
            if (_rateWindow >= 0.5f)
            {
                FireStepsPerSecond = (_spread.StepCount - _rateWindowStart) / _rateWindow;
                _rateWindowStart = _spread.StepCount;
                double windUpdates = 0.0;
                if (_wind != null)
                {
                    // The spin-up steps count as Wind steps for the rates.
                    long windTotal = _wind.StepCount + _wind.SpinUpDone;
                    WindStepsPerSecond = (windTotal - _windRateWindowStart) / _rateWindow;
                    _windRateWindowStart = windTotal;
                    windUpdates = WindStepsPerSecond * (double)_gridWindCells * _wind.PressureIterations;
                }
                CellUpdatesPerSecond = FireStepsPerSecond * (double)_gridFireCells + windUpdates;
                _rateWindow = 0f;
            }
        }
    }
}
