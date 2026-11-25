# KXR-FireCA

KXR-FireCA simulates and renders a wildland fire in a canyon. The project is a Unity 6 HDRP scientific visualization, and the simulation runs in compute shaders on one GPU. The purpose is to show what compute shaders can do.

## What the simulation does

- A 4096 x 4096 Fire Grid at 2 m per cell carries the fire. A Rothermel surface spread model sets the rate of spread in every cell.
- A 256 x 128 x 256 Wind Grid at 32 m per cell carries a 3D wind field. The canyon shapes the wind, the sun drives slope winds, and the fire heats the air.
- A Weather Model drives temperature, humidity, sunlight, and fuel moisture across one simulated day.
- All simulation state lives in GPU textures. Compute shaders do every simulation step.

The project has two Scenarios.

| Scenario | Source |
|---|---|
| `synthetic-canyon-01` | The Canyon Generator of the project. |
| `south-canyon` | Storm King Mountain, Colorado. Elevation from USGS 3DEP, fuel models from LANDFIRE FBFM13, and the Ground Image from NAIP aerial imagery. |

## Download a build

The [Releases page](https://github.com/KXR-LLC/KXR-FireCA/releases) has a standalone player for Windows and for Linux. Each player holds both Scenarios.

| Platform | Graphics API | Start |
|---|---|---|
| Windows 10 or 11, 64 bit | Direct3D 12 | Extract the archive and run `FireCA.exe`. |
| Linux, x86_64 | Vulkan | Extract the archive and run `./FireCA.x86_64`. |

The simulation is sized for one RTX 5090. A GPU with less memory can fail to load a Scenario. The Linux player is built on Windows and is not yet tested on a Linux computer.

## Build from source

The project uses Unity 6000.3.16f1 and HDRP 17.3.0.

1. Open the `unity/` folder with Unity 6000.3.16f1.
2. Run `Tools > FireCA > Setup > Run All Setup Steps`. The step writes the generated assets and the elevation file of `synthetic-canyon-01`.
3. Run the EditMode tests and the PlayMode tests in the Test Runner window.

The elevation file of `south-canyon` is 64 MB and is not in the repository. `data/south-canyon/README.md` describes how to fetch the source data, and `tools/import_canyon.py` writes the Scenario from the source data.

## Documentation

| File | Content |
|---|---|
| `docs/00-overview.md` | Terms, scope, architecture, and fixed decisions. |
| `docs/01-data-layers.md` | Grids, textures, Scenario files, and the Canyon Generator. |
| `docs/02-fire-spread.md` | The Fuel Table, the Rothermel equations, and the Spread Solver. |
| `docs/03-weather-wind.md` | The Weather Model, the Wind Solver, and the fire coupling. |
| `docs/04-visualization.md` | Terrain, fire, smoke, wind, Overlays, and the Control Panel. |
| `docs/05-milestones.md` | Build order, acceptance criteria, and performance targets. |
| `docs/decisions.md` | The decisions made where the specification had a gap. |

`tools/rothermel_reference.py` and `tools/reference_cases.json` are the numeric reference for fire behavior. The tests compare the compute shaders with the reference.

## Limits

KXR-FireCA is a visualization and a demonstration. Do not use KXR-FireCA to predict a real fire or to make a safety decision.

## License

The code is under the MIT License. See `LICENSE`.
