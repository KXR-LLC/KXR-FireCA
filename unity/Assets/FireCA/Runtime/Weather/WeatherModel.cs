using System;
using UnityEngine;

namespace FireCA
{
    /// <summary>
    /// The Weather Model: computes air temperature, relative humidity, sunlight, and fuel moisture
    /// (docs/03-weather-wind.md). The Weather Model updates once per 60 simulated seconds and recomputes
    /// Insolation once per 300 simulated seconds. Until M3, the Weather Model also fills the Surface Wind with
    /// the background wind.
    /// </summary>
    public sealed class WeatherModel
    {
        /// <summary>The Weather Model updates once per 60 simulated seconds.</summary>
        public const double UpdateIntervalS = 60.0;
        /// <summary>The Weather Model recomputes Insolation once per 300 simulated seconds.</summary>
        public const double InsolationIntervalS = 300.0;
        public const int ShadowSteps = 96;
        public const float ShadowStepStartM = 4f;
        public const float ShadowStepGrowth = 1.06f;

        static readonly int FireGridSizeId = Shader.PropertyToID("_FireGridSize");
        static readonly int FireCellSizeId = Shader.PropertyToID("_FireCellSize");
        static readonly int FootprintSizeXId = Shader.PropertyToID("_FootprintSizeX");
        static readonly int FootprintSizeZId = Shader.PropertyToID("_FootprintSizeZ");
        static readonly int FillMoistureId = Shader.PropertyToID("_FillMoisture");
        static readonly int FillWindId = Shader.PropertyToID("_FillWind");
        static readonly int WeatherAId = Shader.PropertyToID("_WeatherA");
        static readonly int WeatherBId = Shader.PropertyToID("_WeatherB");
        static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");
        static readonly int MaxElevationId = Shader.PropertyToID("_MaxElevation");
        static readonly int DtWeatherId = Shader.PropertyToID("_DtWeather");
        static readonly int MoistureMinId = Shader.PropertyToID("_MoistureMin");
        static readonly int MoistureMaxId = Shader.PropertyToID("_MoistureMax");
        static readonly int MoistureWindFactorId = Shader.PropertyToID("_MoistureWindFactor");
        static readonly int ShadowStepsId = Shader.PropertyToID("_ShadowSteps");
        static readonly int ShadowStepStartId = Shader.PropertyToID("_ShadowStepStart");
        static readonly int ShadowStepGrowthId = Shader.PropertyToID("_ShadowStepGrowth");

        readonly ComputeShader _compute;
        readonly TextureRegistry _registry;
        ScenarioData _data;
        float _maxElevation;
        double _lastUpdate = double.NaN;
        double _lastInsolation = double.NaN;
        bool _dirty;
        float? _windSpeedOverride, _windFromOverride, _tempMaxOverride, _tempMinOverride, _rhMinOverride;

        /// <summary>When false, Update changes nothing. The PlayMode fire tests turn the Weather Model off.</summary>
        public bool Enabled = true;

        /// <summary>
        /// When true, the Weather Model fills the Surface Wind with the background wind. The value is true while the
        /// Wind Solver is off. From M3 the Wind Solver writes the Surface Wind.
        /// </summary>
        public bool FillsSurfaceWind = true;

        /// <summary>The HDRP directional light that the Weather Model points along the sun direction. Can be null.</summary>
        public Light SunLight;
        /// <summary>The intensity of the directional light under full direct sunlight.</summary>
        public float SunBaseIntensity = 100000f;

        /// <summary>Fuel moisture fractions. M1 is the initial 1-hour value. The Moisture 1h texture holds the value per cell.</summary>
        public float M1 { get; private set; }
        public float M10 { get; private set; }
        public float M100 { get; private set; }
        public float MLive { get; private set; }

        /// <summary>The background wind at 6.1 m above flat ground, m/s (X, Z). The vector points where the wind goes.</summary>
        public Vector2 BackgroundWind { get; private set; }
        /// <summary>The background wind speed, m/s, and the direction the wind comes from, degrees.</summary>
        public float WindSpeedMps { get; private set; }
        public float WindFromDeg { get; private set; }

        public float TempMaxC => _tempMaxOverride ?? _data.weather.tempMaxC;
        public float TempMinC => _tempMinOverride ?? _data.weather.tempMinC;
        public float RhMinPct => _rhMinOverride ?? _data.weather.rhMinPct;

        /// <summary>The simulation time of the last update, s.</summary>
        public double WeatherTime => double.IsNaN(_lastUpdate) ? 0.0 : _lastUpdate;
        public double SolarHour => WeatherEquations.SolarHour(_data.startSolarHour, WeatherTime);
        /// <summary>The unit vector toward the sun at the last Insolation recompute, world axes.</summary>
        public Vector3 SunDirection { get; private set; } = Vector3.up;
        /// <summary>Air temperature and relative humidity at referenceElevationM, at the last update.</summary>
        public float ReferenceTempC { get; private set; }
        public float ReferenceRhPct { get; private set; }
        public int InsolationRecomputes { get; private set; }

        /// <summary>The weather inputs that the kernels and the terrain shader share. See Weather.hlsl.</summary>
        public Vector4 WeatherA => new Vector4(_data.startSolarHour, (float)WeatherTime, TempMaxC, TempMinC);
        public Vector4 WeatherB => new Vector4(RhMinPct, _data.weather.lapseRateCPerKm, _data.weather.referenceElevationM, 0f);

        public WeatherModel(ComputeShader compute, TextureRegistry registry)
        {
            _compute = compute;
            _registry = registry;
        }

        public void Initialize(ScenarioFiles scenario)
        {
            _data = scenario.Data;
            _maxElevation = scenario.MaxElevation;
            _windSpeedOverride = _windFromOverride = _tempMaxOverride = _tempMinOverride = _rhMinOverride = null;
            var m = _data.moisture;
            SetMoisture(m.m1Pct / 100f, m.m10Pct / 100f, m.m100Pct / 100f, m.livePct / 100f);
            _lastUpdate = double.NaN;
            _lastInsolation = double.NaN;
            bool enabled = Enabled;
            Enabled = true;
            Update(0.0);
            Enabled = enabled;
        }

        // ---- manual weather ----

        /// <summary>True while no manual weather value overrides the Scenario.</summary>
        public bool UseScenarioWeather =>
            _windSpeedOverride == null && _windFromOverride == null && _tempMaxOverride == null
            && _tempMinOverride == null && _rhMinOverride == null;

        /// <summary>Removes every manual weather value.</summary>
        public void RestoreScenarioWeather()
        {
            _windSpeedOverride = _windFromOverride = _tempMaxOverride = _tempMinOverride = _rhMinOverride = null;
            _dirty = true;
        }

        /// <summary>A manual value overrides the Scenario value until RestoreScenarioWeather runs.</summary>
        public void SetManualWind(float speedMps, float fromDeg)
        {
            _windSpeedOverride = Mathf.Max(0f, speedMps);
            _windFromOverride = Mathf.Repeat(fromDeg, 360f);
            _dirty = true;
        }

        public void SetManualTemperature(float tempMaxC, float tempMinC)
        {
            // tempMaxC must be greater than tempMinC (validation check 8).
            _tempMaxOverride = tempMaxC;
            _tempMinOverride = Mathf.Min(tempMinC, tempMaxC - 0.5f);
            _dirty = true;
        }

        public void SetManualRhMin(float rhMinPct)
        {
            _rhMinOverride = Mathf.Clamp(rhMinPct, 1f, 100f);
            _dirty = true;
        }

        // ---- update ----

        /// <summary>
        /// Runs the weather update when 60 simulated seconds have passed, or when a manual value changed.
        /// Returns true when the update ran.
        /// </summary>
        public bool Update(double simSeconds)
        {
            if (!Enabled || _data == null) return false;
            bool first = double.IsNaN(_lastUpdate);
            bool due = first || simSeconds - _lastUpdate >= UpdateIntervalS || simSeconds < _lastUpdate;
            if (!due && !_dirty) return false;

            // A manual change applies at once and does not advance the fuel moisture.
            float dt = due && !first ? (float)Math.Max(0.0, simSeconds - _lastUpdate) : 0f;
            if (due) _lastUpdate = simSeconds;
            _dirty = false;

            // Background wind.
            Vector2 scenarioWind = InterpolateWind(_data.weather.wind, WeatherTime / 3600.0, out float speed, out float fromDeg);
            WindSpeedMps = _windSpeedOverride ?? speed;
            WindFromDeg = _windFromOverride ?? fromDeg;
            Vector2 background = UseScenarioWeather ? scenarioWind : WindVector(WindSpeedMps, WindFromDeg);
            if (FillsSurfaceWind) FillSurfaceWind(background);
            else BackgroundWind = background;

            // Air temperature and relative humidity at the reference elevation.
            double solarHour = SolarHour;
            double tRef = WeatherEquations.ReferenceTempC(TempMaxC, TempMinC, solarHour);
            double rhRef = WeatherEquations.RelativeHumidityPct(WeatherEquations.VaporPressure(RhMinPct, TempMaxC), tRef);
            ReferenceTempC = (float)tRef;
            ReferenceRhPct = (float)rhRef;

            // Insolation, once per 300 simulated seconds.
            if (double.IsNaN(_lastInsolation) || WeatherTime - _lastInsolation >= InsolationIntervalS || WeatherTime < _lastInsolation)
            {
                RecomputeInsolation(solarHour);
                _lastInsolation = WeatherTime;
            }

            if (dt > 0f)
            {
                RunMoistureKernel(dt);
                // m10 and m100 follow the EMC at the reference elevation, with no sunlight term.
                double emc = WeatherEquations.FuelLevelEmc(tRef, rhRef, 0.0, 0.0);
                M10 = (float)WeatherEquations.Relax(M10, emc, dt, WeatherEquations.Tau10hS);
                M100 = (float)WeatherEquations.Relax(M100, emc, dt, WeatherEquations.Tau100hS);
            }
            return true;
        }

        void SetGridUniforms()
        {
            GridSpec grid = _registry.Grid;
            _compute.SetInt(FireGridSizeId, grid.FireCells);
            _compute.SetFloat(FireCellSizeId, GridSpec.FireCellSizeM);
            _compute.SetInt(FootprintSizeXId, grid.WindCellsX);
            _compute.SetInt(FootprintSizeZId, grid.WindCellsZ);
            _compute.SetVector(WeatherAId, WeatherA);
            _compute.SetVector(WeatherBId, WeatherB);
        }

        int FireGroups => (_registry.Grid.FireCells + 7) / 8;

        /// <summary>Computes the sun direction, writes Insolation, and points the directional light.</summary>
        public void RecomputeInsolation(double solarHour)
        {
            WeatherEquations.SunDirection(_data.latitudeDeg, _data.dayOfYear, solarHour, out double sx, out double sy, out double sz);
            SunDirection = new Vector3((float)sx, (float)sy, (float)sz);

            SetGridUniforms();
            int k = _compute.FindKernel("InsolationKernel");
            _compute.SetVector(SunDirectionId, SunDirection);
            _compute.SetFloat(MaxElevationId, _maxElevation);
            _compute.SetInt(ShadowStepsId, ShadowSteps);
            _compute.SetFloat(ShadowStepStartId, ShadowStepStartM);
            _compute.SetFloat(ShadowStepGrowthId, ShadowStepGrowth);
            _compute.SetTexture(k, "_Elevation", _registry.Get(SimTextures.Elevation));
            _compute.SetTexture(k, "_Normal", _registry.Get(SimTextures.Normal));
            _compute.SetTexture(k, "_InsolationOut", _registry.Get(SimTextures.Insolation));
            _compute.Dispatch(k, FireGroups, FireGroups, 1);

            // Surface Heating for the Wind Solver, once per Insolation recompute.
            GridSpec grid = _registry.Grid;
            int heating = _compute.FindKernel("SurfaceHeatingKernel");
            _compute.SetInt("_BlockSize", GridSpec.BlockSize);
            _compute.SetTexture(heating, "_InsolationRead", _registry.Get(SimTextures.Insolation));
            _compute.SetTexture(heating, "_SurfaceHeatingOut", _registry.Get(SimTextures.SurfaceHeating));
            _compute.Dispatch(heating, (grid.WindCellsX + 7) / 8, (grid.WindCellsZ + 7) / 8, 1);
            InsolationRecomputes++;
            UpdateSunLight();
        }

        void UpdateSunLight()
        {
            if (SunLight == null) return;
            // The light keeps a small downward angle at night, and its intensity follows the direct irradiance.
            Vector3 toSun = SunDirection;
            if (toSun.y < 0.02f) toSun = new Vector3(toSun.x, 0.02f, toSun.z).normalized;
            SunLight.transform.rotation = Quaternion.LookRotation(-toSun, Vector3.up);
            float dni = (float)WeatherEquations.DirectNormalIrradiance(SunDirection.y);
            SunLight.intensity = SunBaseIntensity * Mathf.Clamp01(dni / 1000f);
        }

        void RunMoistureKernel(float dt)
        {
            SetGridUniforms();
            int k = _compute.FindKernel("MoistureKernel");
            _compute.SetFloat(DtWeatherId, dt);
            _compute.SetFloat(MoistureMinId, (float)WeatherEquations.MinMoisture1h);
            _compute.SetFloat(MoistureMaxId, (float)WeatherEquations.MaxMoisture1h);
            _compute.SetFloat(MoistureWindFactorId, (float)WeatherEquations.MoistureWindFactor);
            _compute.SetTexture(k, "_Elevation", _registry.Get(SimTextures.Elevation));
            _compute.SetTexture(k, "_InsolationRead", _registry.Get(SimTextures.Insolation));
            _compute.SetTexture(k, "_SurfaceWind", _registry.Get(SimTextures.SurfaceWind));
            _compute.SetTexture(k, "_Moisture1hOut", _registry.Get(SimTextures.Moisture1h));
            _compute.Dispatch(k, FireGroups, FireGroups, 1);
        }

        /// <summary>Sets the fuel moisture fractions and fills Moisture 1h with the 1-hour value.</summary>
        public void SetMoisture(float m1, float m10, float m100, float mLive)
        {
            M1 = m1; M10 = m10; M100 = m100; MLive = mLive;
            GridSpec grid = _registry.Grid;
            int k = _compute.FindKernel("FillMoistureKernel");
            _compute.SetInt(FireGridSizeId, grid.FireCells);
            _compute.SetFloat(FillMoistureId, m1);
            _compute.SetTexture(k, "_Moisture1hOut", _registry.Get(SimTextures.Moisture1h));
            _compute.Dispatch(k, FireGroups, FireGroups, 1);
        }

        /// <summary>Sets the live fuel moisture fraction. The Weather Model holds the value.</summary>
        public void SetLiveMoisture(float mLive) => MLive = mLive;

        /// <summary>Fills the Surface Wind with one wind vector (m/s, pointing where the wind goes).</summary>
        public void FillSurfaceWind(Vector2 wind)
        {
            BackgroundWind = wind;
            GridSpec grid = _registry.Grid;
            int k = _compute.FindKernel("FillSurfaceWindKernel");
            _compute.SetInt(FootprintSizeXId, grid.WindCellsX);
            _compute.SetInt(FootprintSizeZId, grid.WindCellsZ);
            _compute.SetVector(FillWindId, new Vector4(wind.x, wind.y, 0f, 0f));
            _compute.SetTexture(k, "_SurfaceWindOut", _registry.Get(SimTextures.SurfaceWind));
            _compute.Dispatch(k, (grid.WindCellsX + 7) / 8, (grid.WindCellsZ + 7) / 8, 1);
        }

        /// <summary>
        /// The background wind at a time, in hours from Sim Clock zero. The speed interpolates linearly.
        /// The direction interpolates along the shorter arc. The result points where the wind goes.
        /// </summary>
        public static Vector2 InterpolateWind(ScenarioData.WindEntry[] wind, double hours) =>
            InterpolateWind(wind, hours, out _, out _);

        public static Vector2 InterpolateWind(ScenarioData.WindEntry[] wind, double hours, out float speedMps, out float fromDeg)
        {
            speedMps = 0f; fromDeg = 0f;
            if (wind == null || wind.Length == 0) return Vector2.zero;
            if (hours <= wind[0].hour)
            {
                speedMps = wind[0].speedMps; fromDeg = wind[0].fromDeg;
            }
            else if (hours >= wind[wind.Length - 1].hour)
            {
                speedMps = wind[wind.Length - 1].speedMps; fromDeg = wind[wind.Length - 1].fromDeg;
            }
            else
            {
                int i = 0;
                while (i < wind.Length - 2 && hours > wind[i + 1].hour) i++;
                float t = (float)((hours - wind[i].hour) / (wind[i + 1].hour - wind[i].hour));
                speedMps = Mathf.Lerp(wind[i].speedMps, wind[i + 1].speedMps, t);
                fromDeg = Mathf.Repeat(wind[i].fromDeg + Mathf.DeltaAngle(wind[i].fromDeg, wind[i + 1].fromDeg) * t, 360f);
            }
            return WindVector(speedMps, fromDeg);
        }

        /// <summary>The wind vector (X east, Z north) of a speed and a direction the wind comes from.</summary>
        public static Vector2 WindVector(float speedMps, float fromDeg)
        {
            float to = (fromDeg + 180f) * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(to), Mathf.Cos(to)) * speedMps;
        }
    }
}
