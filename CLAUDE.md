# KXR-FireCA

KXR-FireCA is a Unity 6 HDRP scientific visualization of wildland fire spread in a canyon. The simulation runs in compute shaders on one RTX 5090. The first audience is the KXR team, and the purpose is to show what compute shaders can do.

## Read order

1. `docs/00-overview.md`: terms, scope, architecture, fixed decisions.
2. `docs/01-data-layers.md`: grids, textures, Scenario files, Canyon Generator.
3. `docs/02-fire-spread.md`: Fuel Table, Rothermel equations, Spread Solver.
4. `docs/03-weather-wind.md`: Weather Model, Wind Solver, fire coupling.
5. `docs/04-visualization.md`: terrain, fire, smoke, wind, Overlays, Control Panel.
6. `docs/05-milestones.md`: build order, acceptance criteria, performance targets.

## Working rules

1. Build one milestone at a time, in the order that `docs/05-milestones.md` gives.
2. Do not start a milestone until every acceptance criterion of the previous milestone passes.
3. Use the names in the Terms table of `docs/00-overview.md` for classes, shaders, textures, and UI labels.
4. Do not change a Rothermel equation or a Fuel Table value to make a picture look better. Change a Scenario input.
5. Keep the Rothermel core in the native units of the model (ft, lb, min, Btu). Convert to SI at the boundary of the core.
6. Treat `tools/rothermel_reference.py` and `tools/reference_cases.json` as the numeric oracle for fire behavior.
7. When a spec gap blocks the work, write the question and the chosen answer in `docs/decisions.md`, then continue.
8. Record the exact Unity editor version and HDRP version in the Build Notes section below at M0.

## Documentation rules

Documentation in the repository follows `D:\Projects_26\ClaudeWritingGuides\marks-technical-writing-guide.md`. The hard rules are: no em-dashes, no semicolons in prose, one name per thing, and modal verbs by the contract table (must, will, should, can).

## Build Notes

| Item | Value |
|---|---|
| Unity editor | 6000.3.16f1 (revision a56f230f6470), Unity 6.3 LTS |
| HDRP | 17.3.0 (`com.unity.render-pipelines.high-definition`) |
| Project template | `com.unity.template.hdrp-blank` 17.2.5 |
| Graphics API | Direct3D 12 |
| Color space | Linear |
| Input | Input System 1.19.0 |
| Editor automation | `com.unity.pipeline` 1.0.1, driven by the `unity` command line tool |
| Scene | `unity/Assets/FireCA/Scenes/FireCA.unity` |
| UI typeface | B612 Regular and Bold, SIL Open Font License, in `unity/Assets/FireCA/UI/Fonts` (decision D35) |
| Blender | 5.1.2, needed only to build the Plant Models and the Leaf Atlas again (decision D34) |

The steps below rebuild the generated assets of the project.

1. Open `unity/` with Unity 6000.3.16f1.
2. Run `Tools > FireCA > Setup > Run All Setup Steps`. The step writes both Scenarios, the terrain material, the UI panel settings, the sky profile, and the scene.
3. Run the EditMode tests and the PlayMode tests in the Test Runner window.

The command below builds the Plant Models and the Leaf Atlas again (decision D34 in `docs/decisions.md`). The repository holds the two output files, so the command is necessary only after a change to the script.

```text
blender -b --python tools/blender/plant_models.py -- --out unity/Assets/FireCA/Models
```

The command below measures the frame rate in a standalone player (decision D8 in `docs/decisions.md`).

```text
FireCA.exe -popupwindow -screen-fullscreen 0 -screen-width 2560 -screen-height 1440 -scenario synthetic-canyon-01 -firecaBench <report path>
```
