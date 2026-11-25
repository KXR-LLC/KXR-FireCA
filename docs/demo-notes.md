# Demo Notes

## TL;DR

- The demo runs `synthetic-canyon-01` at 2560 x 1440 on one RTX 5090. Every performance target of `05-milestones.md` is met with the default settings. No step of the tuning ladder was applied.
- The player holds 271 frames per second at 60x and 130 frames per second at 240x, with fire coupling on.
- At 240x the simulation runs 34 billion cell updates per second.
- The user starts the scripted demo run with the F5 key, or with the "Start demo run" button of the Control Panel.
- With fire coupling, the fire reaches the top of the chute at 48.5 simulated minutes. Without fire coupling, the time is 62.0 minutes.

## How to Run the Demo

1. Open `unity/` with Unity 6000.3.16f1 and open the scene `Assets/FireCA/Scenes/FireCA.unity`, or start the player `unity/Builds/FireCA/FireCA.exe`.
2. Press Play in the editor. The Scenario loads, and the Wind Solver spin-up runs for about 2 seconds.
3. Press F5. The demo run reloads the Scenario, waits for the spin-up, and runs the four beats.
4. Press F5 again to stop the demo run.

The demo run takes about 2 minutes and 25 seconds of real time.

| Beat | Sim Clock | Time scale | Camera | Real time | What the viewer sees |
|---|---|---|---|---|---|
| 1 | 13:00, paused | 0 | Overview, slow orbit | 14 s | The canyon, the sun shadows, the Wind Particles in the canyon |
| 2 | 13:00 to 13:40 | 60x | Canyon Mouth | 40 s | Ignition on the floor, a fire that follows the channeled wind |
| 3 | 13:40 to 14:30 | 60x | Chute | 50 s | The fire climbs the chute, and the smoke rises along the slope |
| 4 | 14:30 to 17:00 | 240x | Top Down, Arrival time Overlay | 38 s | The whole burn in bands of 10 minutes, and the Stats HUD numbers |

**Note:** The fire enters the chute at 13:31 and reaches the top of the chute at 13:48. Beat 3 starts at 13:40, so the first half of the climb shows in beat 2 from the Canyon Mouth view. A person can move the start of beat 3 to 13:30 in `DemoRun.Beats` if the climb must show from the Chute view alone.

**Note:** Beat 4 hides the Wind Particles and the smoke. From the Top Down view, both cover the Arrival time bands.

## Measured Performance

The standalone player measured every value on 2026-11-24, with the command below. The report is `unity/Builds/m5-benchmark.json`.

```text
FireCA.exe -popupwindow -screen-fullscreen 0 -screen-width 2560 -screen-height 1440 -firecaBench <report path>
```

| Target | Target value | Measured | Result |
|---|---|---|---|
| Frame rate at 60x with fire coupling on | At least 60 frames per second | 271 mean, 122 for the 1 percent low | Met |
| Frame rate at 240x with fire coupling on | At least 30 frames per second | 130 mean, 77 for the 1 percent low | Met |
| Behavior pass, full Fire Grid | Under 4 ms | 0.31 ms | Met |
| Arrival pass, all blocks active | Under 3 ms | 0.33 ms | Met |
| Wind step with 30 pressure iterations | Under 5 ms | 4.85 ms | Met |
| Smoke Pass | Under 3 ms | 1.07 ms | Met |

The table below gives the other measured values.

| Value | Measured |
|---|---|
| Scenario load | 0.39 s |
| Wind Solver spin-up (300 Wind steps) | 1.54 s |
| Frame rate with the Sim Clock paused, lowest of the four camera presets | 370 frames per second |
| Achieved time scale at 60x and at 240x | 60x and 240x |
| Wind steps per second at 240x | 120 |
| Fire steps per second at 240x | 280 |
| Cell updates per second at 60x | 8.6 billion |
| Cell updates per second at 240x | 34.3 billion |
| Texture memory | 1202 MB |

The measurements of the frame rate ran after 45 simulated minutes of fire, with 139 hectares burned and 1148 active blocks. The frame rate at 60x ran from the Chute preset. The frame rate at 240x ran from the Overview preset.

**Note:** The benchmark times each compute pass with a wall clock. The benchmark waits for the GPU, runs the pass 40 times, and waits for the GPU again. The Smoke Pass value is the frame time with the smoke on minus the frame time with the smoke off.

**Note:** At a requested time scale of 1000x, the Wind step budget of 4 limits the achieved time scale to about 320x in the editor. The Stats HUD shows both values.

## Tuning Ladder

No step of the tuning ladder was applied. Every setting keeps the default value.

| Ladder step | Applied |
|---|---|
| 1. Reduce `pressureIterations` from 30 to 20 | No |
| 2. Render the Smoke Pass at half resolution | No |
| 3. Run the full-grid behavior pass once per 2 seconds of real time | No |
| 4. Reduce the Wind step budget from 4 to 2 | No |
| 5. Reduce the Wind Particles count to 1,000,000 | No |

The Wind step is the tightest target, at 4.85 ms against 5 ms. Step 1 of the ladder is the first change to make on slower hardware.

## Final Tunable Values

### Step Scheduler

| Tunable | Value |
|---|---|
| Wind step budget | 4 Wind steps per frame |
| Fire step budget | 8 Fire steps per frame |
| Spin-up Wind steps per frame | 6 |
| Full-grid behavior pass | Once per second of real time |

### Spread Solver

| Tunable | Value |
|---|---|
| `sourceHoldS` | 1800 s |
| `smolderFraction` | 0.05 |
| `smolderDecayS` | 600 s |
| Fire step | `clamp(cellSize / (1.25 * rMax), 0.05, 30)` s |
| Active Mask hold | `sourceHoldS + 60` s (decision D10) |
| `emberFadeS` | 240 s |

### Wind Solver

| Tunable | Value |
|---|---|
| `dtWind` | 2.0 s |
| `g` | 9.81 m/s2 |
| `theta0` | 300 K |
| `rhoCp` | 1105 J/m3/K |
| `z0` | 0.5 m |
| `profileCapM` | 600 m |
| `spongeCells` | 12 |
| `spongeTopCells` | 16 |
| `spongeTimeS` | 20 s |
| `dragCoef` | 0.02 |
| `heatingGain` | 1.0 |
| `couplingGain` | 1.0 |
| `fireConvFraction` | 0.7 |
| `thetaMin`, `thetaMax` | -15 K, 300 K |
| `smokeRate` | 0.002 |
| `smokeLifeS` | 3600 s |
| `pressureIterations` | 30 |
| `sorOmega` | 1.6 |
| `sampleHeightM` | 48 m |
| `spinUpSteps` | 300 |

Every Wind Solver tunable keeps the default value of `03-weather-wind.md`.

### Visualization

| Tunable | Value |
|---|---|
| Wind Particles count | 2,000,000 |
| `particleSpeedScale` | 30 |
| Wind Particles life | 20 s |
| Smoke Pass steps per ray | 96, with 6 light steps |
| Smoke extinction | 0.012 per meter for a smoke density of 1 |
| Smoke density control | 1.0 (range 0 to 4) |
| Overlay opacity | 0.8 |

## Results to Quote

| Result | Value | Source |
|---|---|---|
| Reference Cases matched by the Reference Model | 468 of 468, within 0.1 percent | EditMode test |
| Largest GPU error against the Reference Model | 0.42 percent of the head rate | PlayMode test |
| Fire Grid | 4096 x 4096 cells, 16,777,216 cells, 2 m per cell | Stats HUD |
| Wind Grid | 256 x 128 x 256 cells, 8,388,608 cells, 32 m per cell | Stats HUD |
| 6 simulated hours of fire with a uniform wind | 21 s of real time, 32,512 Fire steps | PlayMode test |
| 6 simulated hours of coupled fire, wind, and weather | 75 s of real time, 10,800 Wind steps, 32,854 Fire steps | PlayMode test |
| Chute top with `couplingGain` 1 and with `couplingGain` 0 | 48.5 min and 62.0 min | PlayMode test |
| Mean upslope Surface Wind in the chute, coupled and not coupled | 1.63 m/s and 0.18 m/s | PlayMode test |

## Known Limits of the Demo

- The Wind Solver is a visualization-grade solver. The wind values and the plume values are not validated against field data.
- The inflow toward a burning disc lasts for about 120 seconds of the plume test and then fades (decision D24).
- The six constants of the EMC equations are not checked against their source (decision D16).
- Wind Particles and the slice plane use compute shaders and hand-written HDRP shaders, not VFX Graph (decision D21).
- The frame rates come from one run of the player on one machine.
