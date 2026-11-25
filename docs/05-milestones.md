# Milestones

## TL;DR

- Six milestones, M0 to M5, take the project from an empty repository to the full demo. M6 adds a real canyon.
- Each milestone ends in a runnable build and a list of acceptance criteria.
- M1 delivers a correct Rothermel fire with a uniform wind. M3 adds terrain-shaped wind. M4 adds fire-coupled wind.
- The performance targets are measured on the Stats HUD. If a target is not met, the tuning ladder gives the order of changes.

## Milestone Summary

| Milestone | Name | Result on screen |
|---|---|---|
| M0 | Terrain | The synthetic canyon in 3D, with fuel colors and Overlays for static layers |
| M1 | Fire | A Rothermel fire spreads under a uniform wind |
| M2 | Weather | The sun moves, shadows cross the canyon, and fuel moisture changes |
| M3 | Terrain wind | A 3D wind field follows the canyon, and Wind Particles show the wind |
| M4 | Fire wind | The fire heats the air, a plume rises, and smoke fills the canyon |
| M5 | Demo | Tuned performance, camera presets, and a scripted demo run |
| M6 | Real canyon | A named canyon from USGS and LANDFIRE data |

## M0: Terrain

Build:

1. Create the Unity 6 HDRP project in `unity/`. Record the editor version and the HDRP version in `CLAUDE.md`.
2. Build the texture registry, the Scenario loader, and the validation checks.
3. Build the Canyon Generator. Write `synthetic-canyon-01` at the 4096 size and `test-canyon-512` at the 512 size.
4. Build the derived layers: Slope, Normal, Ground Height, Solid Mask.
5. Build the Terrain Mesh and the terrain shader with the fuel color layer.
6. Build the camera, the Control Panel shell, and the Stats HUD shell.
7. Build the Fuel model Overlay and the Slope Overlay.

Acceptance criteria:

1. `synthetic-canyon-01` loads in under 10 seconds.
2. Each validation check rejects a Scenario that breaks the check, with the name of the check on screen.
3. The Canyon Generator writes identical files on two runs with the same seed.
4. The synthetic canyon meets Canyon Generator requirements 2 to 10 in `01-data-layers.md`. An editor test measures each requirement.
5. The frame rate is at least 60 frames per second at 2560 x 1440 from every camera preset.

## M1: Fire

Build:

1. Build the Fuel Table and the Reference Model.
2. Build the behavior pass, the arrival pass, the heat flux pass, and the Active Mask.
3. Build the Sim Clock and the Step Scheduler.
4. Fill the Surface Wind with the background wind of the Scenario. Use the initial moisture values of the Scenario.
5. Build Scenario ignitions and the ignite tool.
6. Build the burn scar layer, the fire glow layer, and the Rate of spread, Flame length, and Arrival time Overlays.
7. Build the Time and Ignition sections of the Control Panel and the fire readouts of the Stats HUD.

Acceptance criteria:

1. Both EditMode tests in `02-fire-spread.md` pass.
2. All six PlayMode tests in `02-fire-spread.md` pass.
3. On `synthetic-canyon-01`, a fire from the default ignition runs for 6 simulated hours with no NaN value in any texture.
4. The fire stops at the creek channel and at barren cells.
5. The fire spreads faster up the chute than across the canyon floor.

## M2: Weather

Build:

1. Build the solar time, air temperature, and relative humidity functions as one shared HLSL include.
2. Build the sun position, the shadow ray march, and Insolation.
3. Build the Moisture 1h update and the `m10` and `m100` updates.
4. Drive the HDRP directional light from the sun direction.
5. Build the Fuel moisture, Air temperature, Relative humidity, and Insolation Overlays.
6. Build the Weather section of the Control Panel.

Acceptance criteria:

1. At latitude 37, day 213, the sun elevation at solar noon is 71 degrees, within 1 degree.
2. At 07:00 solar time, the mean Insolation on east-facing cells with a slope over 40 percent is at least 3 times the mean Insolation on west-facing cells with a slope over 40 percent.
3. With the default Scenario, the Moisture 1h of a sunlit cell at 15:00 solar time is lower than the Moisture 1h of a shaded cell at the same elevation.
4. With the default Scenario, Moisture 1h at `referenceElevationM` stays between 1 percent and 20 percent over 24 simulated hours.
5. With a constant background wind, a fire from the default ignition burns more area from 13:00 to 16:00 solar time than from 01:00 to 04:00 solar time.

## M3: Terrain Wind

Build:

1. Build the Wind Solver kernels and the Wind step, with `couplingGain` set to 0.
2. Build Surface Heating in the Weather Model.
3. Build the Surface Wind write. Replace the uniform Surface Wind of M1.
4. Build the spin-up with a progress readout.
5. Build the Wind Particles, the slice plane, and the Surface wind Overlay.
6. Add the wind readouts to the Stats HUD.

Acceptance criteria:

1. **Divergence.** After the project kernel, the mean absolute divergence over the air cells is under 10 percent of the value before the pressure kernel.
2. **Flat flow.** On flat terrain with `heatingGain` set to 0, the Surface Wind after 600 Wind steps matches the background wind within 20 percent in speed and 5 degrees in direction.
3. **Channeling.** With a background wind 45 degrees off the canyon axis and `heatingGain` set to 0, the mean Surface Wind direction on the canyon floor is within 25 degrees of the canyon axis.
4. **Narrow section.** In the same run, the mean Surface Wind speed in the narrow section is higher than the mean speed on the canyon floor 500 m upstream.
5. **Upslope wind.** With a background wind of 0 at 14:00 solar time, after 1800 Wind steps, the Surface Wind points upslope on at least 70 percent of sunlit columns with a slope over 30 percent.
6. **Downslope wind.** With a background wind of 0 at 02:00 solar time, after 1800 Wind steps, the Surface Wind points downslope on at least 70 percent of columns with a slope over 30 percent.
7. **Stability.** The Wind Solver runs 20,000 Wind steps with no NaN value and with a peak speed under 60 m/s.

If criterion 5 or criterion 6 fails, adjust `heatingGain` and `dragCoef` first, and record the final values in `docs/decisions.md`.

## M4: Fire Wind

Build:

1. Build Heat Flux Coarse and the fire term of the ground sources.
2. Build the smoke source and the Smoke Pass.
3. Add the `couplingGain` slider and the smoke controls to the Control Panel.

Acceptance criteria:

1. **Plume.** On flat terrain with a background wind of 0, a burning disc with a radius of 150 m in fuel 4 produces a vertical velocity over 5 m/s above the disc within 300 Wind steps.
2. **Inflow.** In the same run, the Surface Wind 200 m outside the disc edge points toward the disc center, with a speed of at least 0.5 m/s.
3. **Chute.** On `synthetic-canyon-01`, a fire from the default ignition reaches the top of the chute sooner with `couplingGain` set to 1 than with `couplingGain` set to 0. The test report states both times.
4. **Smoke.** The smoke rises from the fire, bends with the wind, and is visible from the Overview preset.
5. **Stability.** A coupled run of 6 simulated hours produces no NaN value, and `theta` stays inside the clamp range.

**Note:** Criterion 3 is the result the demo exists to show. If the coupled fire is not faster, report the two times and the Surface Wind in the chute for both runs. Do not raise `couplingGain` above 2 to force the result.

## M5: Demo

Build:

1. Measure every performance target. Apply the tuning ladder where a target is not met.
2. Build the camera presets and a scripted demo run that the user starts with one key.
3. Build the cell updates readout of the Stats HUD.
4. Write `docs/demo-notes.md` with the measured numbers and the final tunable values.

The scripted demo run has four beats.

| Beat | Sim Clock | Camera | What the viewer sees |
|---|---|---|---|
| 1 | 13:00, paused | Overview, slow orbit | The canyon, the sun shadows, the Wind Particles in the canyon |
| 2 | 13:00 to 13:40 at 60x | Canyon Mouth | Ignition on the floor, an ellipse that follows the channeled wind |
| 3 | 13:40 to 14:30 at 60x | Chute | The fire enters the chute and the plume attaches to the slope |
| 4 | 14:30 to 17:00 at 240x | Top Down, Arrival time Overlay | The whole burn, with 10-minute bands, and the Stats HUD numbers |

### Performance targets

All targets apply to `synthetic-canyon-01` at 2560 x 1440 on the RTX 5090.

| Target | Value |
|---|---|
| Frame rate at 60x with fire coupling on | Should be at least 60 frames per second |
| Frame rate at 240x with fire coupling on | Should be at least 30 frames per second |
| Behavior pass, full Fire Grid | Should take under 4 ms |
| Arrival pass, all blocks active | Should take under 3 ms |
| Wind step with 30 pressure iterations | Should take under 5 ms |
| Smoke Pass | Should take under 3 ms |

The default step budgets are 4 Wind steps and 8 Fire steps per frame.

### Tuning ladder

Apply the changes in the order given. Stop when the targets are met.

1. Reduce `pressureIterations` from 30 to 20.
2. Render the Smoke Pass at half resolution.
3. Run the full-grid behavior pass once per 2 seconds of real time.
4. Reduce the Wind step budget from 4 to 2.
5. Reduce the Wind Particles count to 1,000,000.

## M6: Real Canyon

Build:

1. Write `tools/import_canyon.py`. The script reads a USGS 3DEP elevation GeoTIFF and a LANDFIRE FBFM13 GeoTIFF with GDAL.
2. Resample both rasters to the Fire Grid: bilinear for elevation, nearest neighbor for fuel.
3. Write `elevation.r32`, `fuel.png`, and a `scenario.json` with the latitude of the site.

Acceptance criteria:

1. A Scenario from the script passes every validation check.
2. The fire, weather, and wind systems run on the Scenario with no code change.

**Note:** LANDFIRE fuel data has a 30 m cell. The fuel layer of a real canyon looks blocky at the 2 m Fire Grid cell size.
