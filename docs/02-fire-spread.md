# Fire Spread

## TL;DR

- The Spread Solver computes Rothermel surface fire behavior in every Fire Grid cell, then moves the fire from cell to cell by travel time.
- The Fuel Table holds the 13 Anderson fuel models. The CPU computes the fuel-only constants once. The GPU computes the moisture, wind, and slope terms per cell.
- Wind and slope combine as vectors. The combined vector sets the head direction, the head rate of spread, and an elliptical spread shape.
- A cell ignites at the earliest travel-time arrival from 24 neighbors. The scheme keeps the fire shape elliptical and independent of the step size.
- `tools/rothermel_reference.py` is the numeric oracle. The Reference Model must match the Reference Cases. The GPU must match the Reference Model.

## Fuel Table

### Fuel model parameters

The values come from Anderson (1982), Table 1, and from Albini (1976). Loads are in tons per acre. Surface-area-to-volume ratio (SAV) is in 1/ft. Depth is in ft.

| Code | Name | Load 1-h | Load 10-h | Load 100-h | Load live | SAV 1-h | SAV live | Depth | Dead moisture of extinction (%) |
|---|---|---|---|---|---|---|---|---|---|
| 1 | Short grass | 0.74 | 0.00 | 0.00 | 0.00 | 3500 | 1500 | 1.0 | 12 |
| 2 | Timber grass and understory | 2.00 | 1.00 | 0.50 | 0.50 | 3000 | 1500 | 1.0 | 15 |
| 3 | Tall grass | 3.01 | 0.00 | 0.00 | 0.00 | 1500 | 1500 | 2.5 | 25 |
| 4 | Chaparral | 5.01 | 4.01 | 2.00 | 5.01 | 2000 | 1500 | 6.0 | 20 |
| 5 | Brush | 1.00 | 0.50 | 0.00 | 2.00 | 2000 | 1500 | 2.0 | 20 |
| 6 | Dormant brush | 1.50 | 2.50 | 2.00 | 0.00 | 1750 | 1500 | 2.5 | 25 |
| 7 | Southern rough | 1.13 | 1.87 | 1.50 | 0.37 | 1750 | 1550 | 2.5 | 40 |
| 8 | Closed timber litter | 1.50 | 1.00 | 2.50 | 0.00 | 2000 | 1500 | 0.2 | 30 |
| 9 | Hardwood litter | 2.92 | 0.41 | 0.15 | 0.00 | 2500 | 1500 | 0.2 | 25 |
| 10 | Timber litter and understory | 3.01 | 2.00 | 5.01 | 2.00 | 2000 | 1500 | 1.0 | 25 |
| 11 | Light slash | 1.50 | 4.51 | 5.51 | 0.00 | 1500 | 1500 | 1.0 | 15 |
| 12 | Medium slash | 4.01 | 14.03 | 16.53 | 0.00 | 1500 | 1500 | 2.3 | 20 |
| 13 | Heavy slash | 7.01 | 23.04 | 28.05 | 0.00 | 1500 | 1500 | 3.0 | 25 |

### Model constants

| Constant | Value | Unit |
|---|---|---|
| Particle density `RHO_P` | 32.0 | lb/ft3 |
| Total mineral content `S_T` | 0.0555 | fraction |
| Effective mineral content `S_E` | 0.010 | fraction |
| Heat content `HEAT` | 8000 | Btu/lb |
| SAV 10-h | 109 | 1/ft |
| SAV 100-h | 30 | 1/ft |
| Load conversion | 2000 / 43560 | (lb/ft2) per (ton/acre) |

### Fuel constants

The CPU computes the fuel constants once per fuel code and uploads the constants in one structured buffer of 256 entries. The fuel code is the index. An entry for a code that is not burnable has `burnable` set to 0.

The equations below are normative. Index `j` runs over the three dead classes (1-h, 10-h, 100-h). `w_j` is the load in lb/ft2 and `s_j` is the SAV. `w_l` and `s_l` are the live load and the live SAV.

```text
a_j        = s_j * w_j / RHO_P
a_l        = s_l * w_l / RHO_P
a_dead     = a_1 + a_10 + a_100
f_j        = a_j / a_dead
f_dead     = a_dead / (a_dead + a_l)
f_live     = a_l    / (a_dead + a_l)
sigma      = f_dead * sum_j(f_j * s_j) + f_live * s_l
wn_dead    = sum_j(f_j * w_j) * (1 - S_T)
wn_live    = w_l * (1 - S_T)
rho_b      = (w_1 + w_10 + w_100 + w_l) / depth
beta       = rho_b / RHO_P
beta_op    = 3.348 * sigma^-0.8189
ratio      = beta / beta_op
gamma_max  = sigma^1.5 / (495 + 0.0594 * sigma^1.5)
A          = 133 * sigma^-0.7913
gamma      = gamma_max * ratio^A * exp(A * (1 - ratio))
eta_s      = min(1, 0.174 * S_E^-0.19)
eps_j      = exp(-138 / s_j)
eps_l      = exp(-138 / s_l)
fine_den   = sum_j(w_j * eps_j)
```

| Field | Equation | Use |
|---|---|---|
| `burnable` | 1 for codes 1 to 13, else 0 | Ignition gate |
| `depthFt` | depth | Midflame wind |
| `mxDead` | dead moisture of extinction / 100 | Moisture damping |
| `hasLive` | 1 when `w_l` > 0, else 0 | Live fuel terms |
| `deadMoistureW[3]` | `f_j` | Dead category moisture |
| `fineMoistureW[3]` | `w_j * eps_j / fine_den` | Fine dead moisture |
| `liveRatioW` | `fine_den / (w_l * exp(-500 / s_l))`, or 0 when `w_l` is 0 | Live moisture of extinction |
| `irDeadCoef` | `gamma * wn_dead * HEAT * eta_s` | Reaction intensity |
| `irLiveCoef` | `gamma * wn_live * HEAT * eta_s` | Reaction intensity |
| `xi` | `exp((0.792 + 0.681 * sqrt(sigma)) * (beta + 0.1)) / (192 + 0.2595 * sigma)` | Propagating flux ratio |
| `sinkDeadW[3]` | `rho_b * f_dead * f_j * eps_j` | Heat sink |
| `sinkLiveW` | `rho_b * f_live * eps_l` | Heat sink |
| `windC` | `7.47 * exp(-0.133 * sigma^0.55)` | Wind factor |
| `windB` | `0.02526 * sigma^0.54` | Wind factor |
| `windK` | `ratio^-E`, with `E = 0.715 * exp(-0.000359 * sigma)` | Wind factor |
| `slopeK` | `5.275 * beta^-0.3` | Slope factor |
| `residenceMin` | `384 / sigma` | Residence time |

## Behavior Pass

The behavior pass is the compute kernel that writes Spread A and Spread B. The behavior pass runs the equations in this section for one cell.

### Inputs

| Input | Source | Unit |
|---|---|---|
| Fuel code | Fuel | code |
| `m1` | Moisture 1h | fraction |
| `m10`, `m100`, `mLive` | Uniforms from the Weather Model | fraction |
| Slope gradient | Slope | rise over run |
| Wind at 6.1 m | Surface Wind, bilinear sample | m/s |

### Midflame wind

The Rothermel wind factor uses the wind at midflame height. The behavior pass converts the wind at 6.1 m (20 ft) with the unsheltered wind adjustment factor.

```text
waf            = 1.83 / ln((20 + 0.36 * depthFt) / (0.13 * depthFt))
midflameMps    = waf * |surfaceWind|
U              = midflameMps * 196.850394        (ft/min)
```

### Rothermel equations

```text
damping(r)  = 1 - 2.59 r + 5.11 r^2 - 3.52 r^3,  with r clamped to [0, 1]

mDead       = deadMoistureW[0]*m1 + deadMoistureW[1]*m10 + deadMoistureW[2]*m100
etaDead     = damping(mDead / mxDead)

mFine       = fineMoistureW[0]*m1 + fineMoistureW[1]*m10 + fineMoistureW[2]*m100
mxLive      = max(mxDead, 2.9 * liveRatioW * (1 - mFine / mxDead) - 0.226)
etaLive     = hasLive ? damping(mLive / mxLive) : 0

IR          = irDeadCoef * etaDead + irLiveCoef * etaLive              (Btu/ft2/min)
sink        = sinkDeadW[0]*(250 + 1116*m1) + sinkDeadW[1]*(250 + 1116*m10)
            + sinkDeadW[2]*(250 + 1116*m100) + sinkLiveW*(250 + 1116*mLive)
R0          = IR * xi / sink                                           (ft/min)

phiW        = U > 0 ? windC * U^windB * windK : 0
phiS        = slopeK * slope^2,  with slope = |gradient|
```

### Wind and slope vectors

The wind factor points where the wind goes. The slope factor points upslope. The behavior pass adds the two vectors.

```text
windDir     = normalize(surfaceWind)                     (X, Z)
upslopeDir  = normalize(gradient)                        (X, Z)
V           = phiW * windDir + phiS * upslopeDir
phiE        = |V|
headDir     = phiE > 0 ? V / phiE : (0, 0)
Ueff        = phiE > 0 ? (phiE / (windC * windK))^(1 / windB) : 0     (ft/min)

if Ueff > 0.9 * IR:                                      (wind limit)
    Ueff    = 0.9 * IR
    phiE    = windC * Ueff^windB * windK

Rhead       = R0 * (1 + phiE)                            (ft/min)
LB          = min(8, 1 + 0.25 * (Ueff / 88))             (length-to-breadth ratio)
ecc         = sqrt(LB^2 - 1) / LB
```

### Outputs

```text
tR          = residenceMin * 60                          (s)
byram       = IR * residenceMin * Rhead / 60             (Btu/ft/s)
flameFt     = byram > 0 ? 0.45 * byram^0.46 : 0

Spread A    = (Rhead * 0.00508, headDir.x, headDir.z, ecc)
Spread B    = (IR * 0.189276, tR, flameFt * 0.3048, R0 * 0.00508)
```

The requirements below apply to the behavior pass.

1. The behavior pass must write zero to all channels of Spread A and Spread B for a cell that is not burnable.
2. The behavior pass must keep every intermediate value in the units that the equations state.
3. The behavior pass must clamp `m1` to the range 0.01 to 0.60 before the equations run.
4. The behavior pass must produce no NaN value and no infinite value for any input in the valid ranges.
5. The Spread Solver must run the behavior pass on every active block in every Fire step.
6. The Spread Solver must run the behavior pass on the full Fire Grid at least once per second of real time.

### Rate at an angle

The rate of spread in a direction `d` (a unit vector on X and Z) follows an ellipse with the ignition point at the rear focus.

```text
rateAt(cell, d) = Rhead * (1 - ecc) / (1 - ecc * dot(headDir, d))
```

When `phiE` is 0, `ecc` is 0 and the rate equals `R0` in every direction.

## Arrival Pass

The arrival pass is the compute kernel that moves the fire. The arrival pass reads the read copy of the Arrival Time and writes the write copy.

For each cell, the arrival pass finds the earliest time at which fire from an ignited neighbor reaches the cell. The neighbors are the 24 cells of the 5 x 5 block around the cell. The 24 neighbors give 16 distinct directions, so a fire on flat ground with no wind grows as a near circle. The largest shape error of the 24-neighbor scheme is about 2.7 percent, in the directions between two neighbor directions.

```text
NOT_IGNITED = 1.0e30

arrival(c):
    if ArrivalRead[c] < 1.0e29:            return ArrivalRead[c]
    if not burnable(c):                    return NOT_IGNITED
    if SpreadA[c].Rhead <= 0:              return NOT_IGNITED

    best = NOT_IGNITED
    for each offset o in the 5 x 5 block, o != (0, 0):
        n  = c + o
        if n is outside the Fire Grid:     continue
        tn = ArrivalRead[n]
        if tn >= 1.0e29:                   continue
        if now - tn > sourceHoldS:         continue
        if not pathIsBurnable(n, c):       continue

        d    = normalize(c - n)                                  (X, Z)
        dist = sqrt((|o| * cellSize)^2 + (Elevation[c] - Elevation[n])^2)
        rn   = rateAt(n, d)
        rc   = rateAt(c, d)
        if rn <= 0 or rc <= 0:             continue
        r    = 2 * rn * rc / (rn + rc)
        best = min(best, tn + dist / r)

    if best <= now + dt:                   return max(best, now)
    return NOT_IGNITED
```

The requirements below apply to the arrival pass.

1. The arrival pass must use the harmonic mean of the source rate and the destination rate, as the pseudocode shows.
2. The arrival pass must use the 3D surface distance between cell centers.
3. `pathIsBurnable` must return false when a cell between the source and the destination is not burnable. For an offset of (2, 0), the cell between is (1, 0). For an offset of (2, 2), the cell between is (1, 1). For an offset of (2, 1), the cells between are (1, 0) and (1, 1), and both must be burnable.
4. A cell that ignited more than `sourceHoldS` seconds ago must not ignite a neighbor. The default value of `sourceHoldS` is 1800.
5. The arrival pass must never change the Arrival Time of a cell that is already ignited.
6. The arrival pass must skip a cell when the Active Mask of the block of the cell is 0.

### Fire step size

A fire front must not cross more than one cell in one Fire step. The Spread Solver computes the step size from the fastest active cell.

1. Compute `rMax`, the largest head rate of spread in the active blocks, with a GPU reduction.
2. Read `rMax` back with `AsyncGPUReadback`. Use the newest value that is available.
3. Set `dt = clamp(cellSize / (1.25 * rMax), 0.05, 30)` seconds.
4. If no cell is ignited, do not run the arrival pass.

### Active Mask

The Active Mask limits the arrival pass and the behavior pass to the blocks near the fire. A cell is active when the cell ignited less than `sourceHoldS` seconds ago.

1. After each Fire step, set the Active Mask of a block to 1 when the block or one of the 8 blocks around the block holds an active cell.
2. Report the count of active blocks to the Stats HUD.

### Ignition

1. At the `hour` of each entry in `ignitions`, set the Arrival Time of every burnable cell in the circle to the Sim Clock value.
2. When the user clicks the terrain with the ignite tool, set the Arrival Time of every burnable cell within 6 m of the hit point to the Sim Clock value.
3. Do not ignite a cell that is already ignited.
4. After an ignition, update the Active Mask before the next Fire step.

### Cell state

The Arrival Time and the Sim Clock define the state of a cell. No separate state texture exists.

| State | Condition |
|---|---|
| Unburned | Arrival Time is `1.0e30` |
| Flaming | `now - arrival` is less than the residence time of the cell |
| Smoldering | `now - arrival` is at least the residence time and less than `sourceHoldS` |
| Burned out | `now - arrival` is at least `sourceHoldS` |

## Heat Flux Pass

The heat flux pass writes the Heat Flux after each Fire step. The Wind Solver and the fire glow both read the Heat Flux.

A Fire step can be longer than the residence time of a cell. The heat flux pass therefore averages the flaming heat over the step, so the total energy is correct for any step size.

```text
age        = now - arrival
overlap    = max(0, min(now + dt, arrival + tR) - max(now, arrival)) / dt
smolder    = age > tR ? smolderFraction * exp(-(age - tR) / smolderDecayS) : 0
HeatFlux   = IR_kW * (overlap + smolder)
```

| Tunable | Default |
|---|---|
| `smolderFraction` | 0.05 |
| `smolderDecayS` | 600 |

1. The heat flux pass must write 0 for a cell that is unburned.
2. The heat flux pass must write 0 when the result is less than 1 kW/m2.
3. After the heat flux pass, the Spread Solver must write Heat Flux Coarse as the mean of the Heat Flux over each 16 x 16 block.

**Note:** The Spread Solver samples the reaction intensity at the current time, not at the time of ignition. The difference is small because the fuel moisture changes slowly.

## Reference Model and Tests

The Reference Model is a C# class with no Unity dependency. The Reference Model and the Fuel Table upload code share one implementation of the fuel constants.

`tools/reference_cases.json` holds 468 cases: 13 fuel models, 3 moisture sets, 4 winds, and 3 slopes. Each case has an `in` object and an `out` object in SI units.

### EditMode tests

1. For every Reference Case, each `out` value of the Reference Model must match the file within 0.1 percent, or within 0.000001 absolute.
2. The Reference Model must return a head rate of spread of 0 when `m1`, `m10`, and `m100` all equal `mxDead` and the fuel has no live load.

### PlayMode tests

Each test uses a 512 Fire Grid. Each test turns the Weather Model and the Wind Solver off. Each test uses the moisture values 8, 8, 8, and 100 percent (1-h, 10-h, 100-h, live). When a test states a midflame wind, the test fills the Surface Wind with the midflame wind divided by `waf`.

1. **GPU match.** Fill one cell per Reference Case with the case inputs. Run the behavior pass. Each channel of Spread A and Spread B must match the Reference Model within 1 percent.
2. **Circle.** Flat terrain, fuel 1, no wind. Ignite the center cell. After 3600 simulated seconds, the distance from the center to the fire edge must equal `R0 * 3600` within 4 percent in each of 16 directions.
3. **Ellipse.** Flat terrain, fuel 1, uniform midflame wind of 2 m/s toward the east. Ignite one cell 200 m from the west edge. After 600 simulated seconds, the head distance, the back distance, and the largest width must each match the analytic ellipse within 6 percent or within 4 m, whichever tolerance is larger.
4. **Slope.** A plane with a slope of 30 percent, fuel 5, no wind. The head direction must be upslope. The head rate must match the Reference Model within 4 percent.
5. **Barrier.** A strip of fuel code 99, 2 cells wide, across the grid. The fire must not cross the strip.
6. **Step independence.** Run the ellipse test with the Fire step forced to 0.25 of the computed value. The burned area must match the first run within 2 percent.

The analytic ellipse for test 3 is below. `t` is the time from ignition. For the conditions of test 3, the Reference Model gives a head rate of 0.364 m/s and an eccentricity of 0.882, so the head distance is 218 m, the back distance is 14 m, and the width is 110 m.

```text
head   = Rhead * t
back   = Rhead * t * (1 - ecc) / (1 + ecc)
width  = (head + back) * sqrt(1 - ecc^2)
```

### Published check

The table compares the equations in this document with the values that Anderson (1982) published. The conditions are: midflame wind 5 mi/h, dead fuel moisture 8 percent, live fuel moisture 100 percent, no slope. The values in the "This document" columns come from `tools/rothermel_reference.py`.

| Code | Rate of spread, this document (chains/h) | Rate of spread, Anderson | Flame length, this document (ft) | Flame length, Anderson |
|---|---|---|---|---|
| 1 | 81.1 | 78 | 4.1 | 4 |
| 2 | 32.5 | 35 | 5.9 | 6 |
| 3 | 102.1 | 104 | 12.1 | 12 |
| 4 | 71.4 | 75 | 18.7 | 19 |
| 5 | 14.5 | 18 | 3.8 | 4 |
| 6 | 29.6 | 32 | 5.6 | 6 |
| 7 | 25.9 | 20 | 5.5 | 5 |
| 8 | 1.8 | 1.6 | 1.0 | 1.0 |
| 9 | 7.6 | 7.5 | 2.7 | 2.6 |
| 10 | 7.9 | 7.9 | 4.8 | 4.8 |
| 11 | 5.6 | 6.0 | 3.3 | 3.5 |
| 12 | 11.9 | 13.0 | 7.6 | 8.0 |
| 13 | 14.2 | 13.5 | 9.7 | 10.5 |

**Note:** Most models agree within 10 percent. Models 5 and 7 differ more. The 1982 values came from nomograms, and the live fuel treatment changed in later versions of the model. Do not tune the equations to close the gap.

**Note:** A CPU prototype of the arrival pass passed tests 2, 3, and 6 before this document was written. The circle test error was 1 percent. The ellipse head and width errors were under 2 percent, and the back distance error was 1.7 m.

## Known Approximations

- The arrival pass uses the current rate of spread for the whole path between two cells. A fast wind shift moves the front slightly early or slightly late.
- The Rothermel model is a steady-state surface fire model. The model does not produce the acceleration phase of a new fire.
- The wind limit of `0.9 * IR` follows Rothermel (1972). Later work questions the limit. The limit stays on for the first six milestones.
- The length-to-breadth equation `1 + 0.25 * Ueff` is the simple linear form. The cap of 8 prevents a degenerate ellipse.
