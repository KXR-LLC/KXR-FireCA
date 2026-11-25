using System;

namespace FireCA
{
    /// <summary>The Sim Clock: the simulation time, in seconds from the start of the Scenario.</summary>
    public sealed class SimClock
    {
        public const float MinTimeScale = 1f;
        public const float MaxTimeScale = 1000f;
        public const float DefaultTimeScale = 60f;

        /// <summary>Seconds from the start of the Scenario.</summary>
        public double Seconds { get; private set; }
        public bool Playing { get; set; }
        /// <summary>The requested time scale: simulated seconds per real second.</summary>
        public float TimeScale { get; private set; } = DefaultTimeScale;
        /// <summary>The time scale that the last frames achieved. The value is lower when a step budget is spent.</summary>
        public float AchievedTimeScale { get; private set; }
        /// <summary>Local solar time at Sim Clock zero, hours.</summary>
        public float StartSolarHour { get; private set; }

        public void Reset(float startSolarHour)
        {
            Seconds = 0.0;
            StartSolarHour = startSolarHour;
            AchievedTimeScale = 0f;
        }

        public void SetTimeScale(float scale) => TimeScale = Math.Min(MaxTimeScale, Math.Max(MinTimeScale, scale));

        /// <summary>Sets the Sim Clock. The Step Scheduler is the only caller in a normal run.</summary>
        public void Advance(double targetSeconds, float frameSeconds)
        {
            double advance = Math.Max(0.0, targetSeconds - Seconds);
            Seconds = Math.Max(Seconds, targetSeconds);
            if (frameSeconds > 0f)
            {
                float achieved = (float)(advance / frameSeconds);
                AchievedTimeScale += (achieved - AchievedTimeScale) * Math.Min(1f, frameSeconds * 4f);
            }
        }

        /// <summary>Local solar time, hours from 0 to 24.</summary>
        public double SolarHour => (StartSolarHour + Seconds / 3600.0) % 24.0;

        /// <summary>The day of the run, from 1.</summary>
        public int Day => 1 + (int)Math.Floor((StartSolarHour + Seconds / 3600.0) / 24.0);

        /// <summary>The readout of the Control Panel: the day and the solar time.</summary>
        public string Readout
        {
            get
            {
                double hour = SolarHour;
                int h = (int)hour, m = (int)((hour - h) * 60.0), s = (int)((hour - h) * 3600.0) % 60;
                return $"Day {Day}, {h:00}:{m:00}:{s:00} solar time";
            }
        }
    }
}
