# KXR-FireCA Overview

## TL;DR

- KXR-FireCA simulates and renders a wildland fire in a canyon, in Unity 6 with HDRP, on one RTX 5090.
- A 4096 x 4096 Fire Grid at 2 m per cell carries the fire. A Rothermel surface spread model sets the rate of spread in every cell.
- A 256 x 128 x 256 Wind Grid at 32 m per cell carries a 3D wind field. The canyon shapes the wind, the sun drives slope winds, and the fire heats the air.
- A Weather Model drives temperature, humidity, sunlight, and fuel moisture across one simulated day.
- All simulation state lives in GPU textures. Compute shaders do every simulation step. The CPU schedules steps and loads files.
- The first Scenario is a synthetic canyon. A real canyon uses the same file format later.
- The build has six milestones. Each milestone runs as a demo.

## Terms

Each term has one meaning in the project. Use each term exactly as written.

| Term | Meaning |
|---|---|
| Fire Grid | The 2D grid of 4096 x 4096 cells, 2 m per cell, that covers the terrain surface. |
| Wind Grid | The 3D grid of 256 x 128 x 256 cells, 32 m per cell, that covers the air above the terrain. |
| Scenario | One folder of input files: an elevation file, a fuel file, and a `scenario.json` file. |
| Canyon Generator | The editor tool that writes a synthetic Scenario. |
| Ground Image | The optional color image of the ground of a Scenario. The terrain shows the Ground Image in place of the fuel color. |
| Fuel Table | The table of fuel model parameters and the constants computed from the parameters. |
| Spread Solver | The compute shader system that computes fire behavior and moves the fire across the Fire Grid. |
| Spread A, Spread B | The two Fire Grid textures that hold the per-cell fire behavior values. |
| Arrival Time | The Fire Grid texture that holds the simulation time at which each cell ignited. |
| Heat Flux | The Fire Grid texture that holds the heat release of each cell, in kW/m2. |
| Weather Model | The system that computes air temperature, relative humidity, sunlight, and fuel moisture. |
| Wind Solver | The compute shader system that advances the 3D wind field on the Wind Grid. |
| Surface Wind | The 256 x 256 texture that holds the horizontal wind at 6.1 m above the ground. |
| Sim Clock | The simulation time, in seconds from the start of the Scenario. |
| Step Scheduler | The CPU component that decides how many solver steps run in each frame. |
| Reference Model | The CPU implementation of the Rothermel equations in C#, used for tests. |
| Reference Cases | The 468 input and output pairs in `tools/reference_cases.json`. |
| Terrain Mesh | The rendered mesh of the canyon. |
| Overlay | One data layer drawn in color on the Terrain Mesh. |
| Wind Particles | The GPU particles that trace the wind field. |
| Smoke Pass | The HDRP Custom Pass that draws the smoke volume. |
| Control Panel | The runtime UI for Scenario, time, weather, ignition, and display controls. |
| Stats HUD | The runtime readout of cell counts, step times, and time scale. |

## Scope

KXR-FireCA will do the items in this list.

1. Load a Scenario and render the terrain in 3D.
2. Compute surface fire spread with the Rothermel model and the 13 Anderson fuel models.
3. Compute fuel moisture from temperature, relative humidity, and sunlight.
4. Compute a 3D wind field that responds to the terrain, to slope heating, and to fire heat.
5. Show the fire, the smoke, the wind, and the weather fields in one 3D view.
6. Show the size and speed of the computation on the Stats HUD.

KXR-FireCA will not do the items in this list in the first six milestones.

1. Crown fire.
2. Spotting from embers.
3. Suppression actions.
4. Canopy sheltering of wind and sunlight.
5. VR or mixed reality output.
6. Operational fire forecasts.

**Note:** The Wind Solver is a visualization-grade fluid solver. The Wind Solver is not a validated atmospheric model. The Rothermel spread values are quantitatively checked against the Reference Cases. The wind values are plausible and are not validated against field data.

## Architecture

The simulation has three systems. Each system owns the textures that the system writes.

- The **Weather Model** reads the Scenario and the Sim Clock. The Weather Model writes air temperature, relative humidity, sunlight, and fuel moisture.
- The **Wind Solver** reads the terrain, the sunlight, and the Heat Flux. The Wind Solver writes the 3D velocity, the smoke density, and the Surface Wind.
- The **Spread Solver** reads the fuel, the slope, the fuel moisture, and the Surface Wind. The Spread Solver writes Spread A, Spread B, the Arrival Time, and the Heat Flux.

The edge list below is the normative data flow.

```text
Scenario        -> Elevation, Fuel, Slope, Normal, Solid Mask
Scenario        -> Weather Model (temperature range, humidity, background wind, date, latitude)
Weather Model   -> Insolation, Moisture 1h, global moisture values
Weather Model   -> Wind Solver (background wind, surface heating)
Wind Solver     -> Velocity, Smoke, Surface Wind
Surface Wind    -> Spread Solver
Moisture 1h     -> Spread Solver
Spread Solver   -> Spread A, Spread B, Arrival Time, Heat Flux
Heat Flux       -> Wind Solver (fire coupling)
All textures    -> Visualization (read only)
```

The loop from the Spread Solver to the Wind Solver and back is the fire coupling. The fire heats the air, the hot air rises, the rising air pulls wind toward the fire, and the new wind changes the spread.

## Frame Loop

The Step Scheduler runs the steps below in each frame, in the order given.

1. Read the time scale from the Control Panel. Compute the target Sim Clock advance: frame time multiplied by time scale.
2. Run Wind Solver steps until the wind time reaches the Sim Clock target, or until the wind step budget for the frame is spent.
3. If the wind step budget is spent, reduce the Sim Clock target to the wind time.
4. Run the Weather Model update if 60 simulated seconds have passed since the last update.
5. Run Spread Solver steps until the fire time reaches the Sim Clock target, or until the fire step budget for the frame is spent.
6. If the fire step budget is spent, reduce the Sim Clock target to the fire time.
7. Set the Sim Clock to the target.
8. Update the Wind Particles, then render.

The Stats HUD will show the requested time scale and the achieved time scale. The two values differ when a step budget is spent.

## Fixed Decisions

| Decision | Value |
|---|---|
| Engine | Unity 6 LTS |
| Render pipeline | HDRP |
| Target hardware | One RTX 5090 (32 GB), Windows, DirectX 12 |
| Target frame rate | 60 frames per second at 2560 x 1440 |
| Fire Grid | 4096 x 4096 cells, 2 m per cell, 8192 m per side |
| Wind Grid | 256 x 128 x 256 cells, 32 m per cell, 4096 m tall |
| World axes | X is east, Y is up, Z is north. One Unity unit is 1 m. |
| World origin | The southwest corner of the Fire Grid, at elevation 0 m |
| Azimuth | Degrees clockwise from north. North is +Z. East is +X. |
| Wind direction in files | The direction the wind comes from (meteorological convention) |
| Wind direction in code | A vector that points where the wind goes |
| Simulation units | SI everywhere, except inside the Rothermel core |
| Moisture values | Fractions in code (0.06). Percent in `scenario.json` and in the UI (6). |
| Fuel models | The 13 Anderson (1982) fire behavior fuel models |
| UI framework | UI Toolkit (runtime) |
| Input | The Input System package |
| Tests | Unity Test Framework, EditMode and PlayMode |

## Repository Layout

```text
KXR-FireCA/
  CLAUDE.md
  docs/
    00-overview.md
    01-data-layers.md
    02-fire-spread.md
    03-weather-wind.md
    04-visualization.md
    05-milestones.md
    decisions.md
  tools/
    rothermel_reference.py
    reference_cases.json
  unity/
    Assets/
      FireCA/
        Runtime/
          Core/          (Sim Clock, Step Scheduler, texture registry)
          Scenario/      (Scenario loader, validation)
          Spread/        (Fuel Table, Reference Model, Spread Solver)
          Weather/       (Weather Model)
          Wind/          (Wind Solver)
          Viz/           (Terrain Mesh, Overlays, Smoke Pass, Wind Particles)
          UI/            (Control Panel, Stats HUD)
        Shaders/
          Compute/       (.compute files and shared .hlsl includes)
          Graphs/        (Shader Graph assets)
        Editor/          (Canyon Generator)
        Tests/
          EditMode/
          PlayMode/
      StreamingAssets/
        Scenarios/
          synthetic-canyon-01/
```

## Sources

- Rothermel, R. C. 1972. A mathematical model for predicting fire spread in wildland fuels. USDA Forest Service Research Paper INT-115.
- Albini, F. A. 1976. Estimating wildfire behavior and effects. USDA Forest Service General Technical Report INT-30.
- Anderson, H. E. 1982. Aids to determining fuel models for estimating fire behavior. USDA Forest Service General Technical Report INT-122.
- Rothermel, R. C., and others. 1986. Modeling moisture content of fine dead wildland fuels: input to the BEHAVE fire prediction system. USDA Forest Service Research Paper INT-359.
- Andrews, P. L. 2018. The Rothermel surface fire spread model and associated developments: a comprehensive explanation. USDA Forest Service General Technical Report RMRS-GTR-371.
