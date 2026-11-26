# Weather and Wind

## TL;DR

- The Weather Model computes air temperature, relative humidity, sun position, sunlight on every cell, and fuel moisture. The Weather Model updates once per 60 simulated seconds.
- The Wind Solver is one 3D fluid solver on the Wind Grid. The same solver produces terrain-shaped wind and fire-coupled wind.
- Terrain shapes the wind because the terrain is a solid boundary in the solver. Sunlit slopes heat the air and drive upslope wind. Shaded slopes cool the air and drive downslope wind.
- Fire coupling is one more heat source. The Heat Flux of the fire heats the air, and the solver does the rest.
- The Wind Solver writes the Surface Wind. The Spread Solver reads the Surface Wind.

## Weather Model

### Solar time and air temperature

```text
solarHour = (startSolarHour + simClock / 3600) mod 24

tMean     = (tempMaxC + tempMinC) / 2
tAmp      = (tempMaxC - tempMinC) / 2
tRef      = tMean + tAmp * cos(2 * pi * (solarHour - 15) / 24)          (deg C)

airTempC(cell) = tRef - lapseRateCPerKm * (elevation - referenceElevationM) / 1000
```

The temperature peaks at 15:00 solar time.

### Relative humidity

The Weather Model holds the water vapor pressure constant through the day. The humidity falls when the air warms and rises when the air cools.

```text
es(T)     = 6.112 * exp(17.67 * T / (T + 243.5))                        (hPa, T in deg C)
e         = (rhMinPct / 100) * es(tempMaxC)
rhPct(cell) = clamp(100 * e / es(airTempC(cell)), 1, 100)
```

### Sun position

`N` is `dayOfYear`. `lat` is `latitudeDeg`. The sun direction is a unit vector in world axes (X east, Y up, Z north).

```text
decl      = 23.44 deg * sin(2 * pi * (284 + N) / 365)
hourAngle = 15 deg * (solarHour - 12)

sun.y     = sin(lat) * sin(decl) + cos(lat) * cos(decl) * cos(hourAngle)
sun.x     = -cos(decl) * sin(hourAngle)
sun.z     = cos(lat) * sin(decl) - sin(lat) * cos(decl) * cos(hourAngle)
```

### Insolation

```text
dni        = sun.y > 0.02 ? 1361 * 0.75^(1 / sun.y) : 0                 (W/m2)
Insolation = dni * max(0, dot(normal, sun)) * shadow
```

1. The Weather Model must compute `shadow` with a ray march over Elevation from the cell toward the sun.
2. The ray march must use 96 steps. The step length must start at 4 m and grow by 6 percent per step.
3. `shadow` must be 0 when the terrain is above the ray at any step, and 1 otherwise.
4. The Weather Model must recompute Insolation once per 300 simulated seconds.
5. The Weather Model must set the HDRP directional light to the sun direction at each recompute.

### Fuel moisture

The 1-hour fuel moisture follows the equilibrium moisture content (EMC) of the air at the fuel. Sunlight heats the fuel and dries the air at the fuel surface. The equations use deg F and percent, the units of the source model.

```text
taF     = airTempC * 9 / 5 + 32
iCal    = Insolation / 697.8                                (cal/cm2/min)
uMph    = midflameMps * 2.23694
tfF     = taF + iCal / (0.015 * uMph + 0.026)               (fuel-level temperature)
rhF     = rhPct * exp(-0.033 * (tfF - taF))                 (fuel-level humidity)

EMC (percent), with H = rhF and T = tfF:
    H < 10:        0.03229 + 0.281073 * H - 0.000578 * H * T
    10 <= H < 50:  2.22749 + 0.160107 * H - 0.014784 * T
    H >= 50:       21.0606 + 0.005565 * H^2 - 0.00035 * H * T - 0.483199 * H

emc     = EMC / 100
m1      = m1 + (emc - m1) * (1 - exp(-dtWeather / 3600))
```

1. The Weather Model must update Moisture 1h for every Fire Grid cell once per 60 simulated seconds.
2. The Weather Model must clamp Moisture 1h to the range 0.01 to 0.35.
3. The Weather Model must update `m10` with the same equation and a time constant of 36,000 s.
4. The Weather Model must update `m100` with the same equation and a time constant of 360,000 s.
5. For `m10` and `m100`, the Weather Model must use the EMC at `referenceElevationM` with `iCal` set to 0.
6. The Weather Model must hold `mLive` at the Scenario value.
7. The Weather Model must use a midflame wind of 0.4 times the Surface Wind speed in the fuel moisture equations.

**Note:** The fuel-level temperature and humidity equations and the EMC equations come from the BEHAVE fine dead fuel moisture model (Rothermel and others 1986). Check the constants against the publication before M2 closes, and record the result in `docs/decisions.md`.

### Surface Heating

The Weather Model writes Surface Heating for the Wind Solver once per Insolation recompute.

```text
SurfaceHeating = 0.35 * mean(Insolation over the 16 x 16 block) - 30     (W/m2)
```

A sunlit slope gets a positive value. A shaded slope and every slope at night get -30 W/m2.

### Background wind

The Weather Model interpolates the `wind` array of the Scenario by time. The Weather Model interpolates the speed linearly and the direction along the shorter arc. The result is the background wind at 6.1 m above flat ground.

## Wind Solver

The Wind Solver is an incompressible fluid solver with buoyancy, on a collocated grid. Each Wind Grid cell holds a velocity, a potential temperature difference `theta` (K above the background air at the same height), and a smoke density.

### Constants and tunables

| Name | Default | Meaning |
|---|---|---|
| `dtWind` | 2.0 s | Fixed Wind step |
| `g` | 9.81 m/s2 | Gravity |
| `theta0` | 300 K | Reference potential temperature |
| `rhoCp` | 1105 J/m3/K | Air density (1.1 kg/m3) times heat capacity (1005 J/kg/K) |
| `z0` | 0.5 m | Roughness length of the background wind profile |
| `profileCapM` | 600 m | Height above which the background wind stops increasing |
| `spongeCells` | 12 | Width of the lateral relaxation zone |
| `spongeTopCells` | 16 | Depth of the top relaxation zone |
| `spongeTimeS` | 20 s | Relaxation time at the outer face |
| `dragCoef` | 0.02 | Surface drag coefficient |
| `heatingGain` | 1.0 | Multiplier on Surface Heating |
| `couplingGain` | 1.0 | Multiplier on fire heat. The value 0 turns fire coupling off. |
| `fireConvFraction` | 0.7 | Fraction of fire heat that heats the air |
| `thetaMin`, `thetaMax` | -15 K, 300 K | Clamp range of `theta` |
| `smokeRate` | 0.002 | Smoke added per kW/m2 per second |
| `smokeLifeS` | 3600 s | Smoke decay time |
| `pressureIterations` | 30 | Iterations of the pressure solve |
| `sorOmega` | 1.6 | Over-relaxation factor |
| `sampleHeightM` | 48 m | Height above Ground Height for the Surface Wind sample |
| `spinUpSteps` | 300 | Wind steps that run before the Sim Clock starts |
| `advectionSharpness` | 1.0 | Share of the correction of the advection, from 0 to 1 (`decisions.md`, decision D31) |
| `vorticityConfinement` | 0 | Vorticity confinement factor, from 0 to 0.06. The value 0 turns the force off. |

### Ground cells

A ground cell is an air cell with a solid cell directly below. Each Wind Grid column has one ground cell. The ground cell receives the surface heat, the fire heat, the smoke, and the drag.

### Background wind profile

`h` is the height of a cell center above Ground Height. `bg` is the background wind vector at 6.1 m, pointing where the wind goes.

```text
hc           = clamp(h, 0, profileCapM)
profile(h)   = ln((hc + z0) / z0) / ln((6.1 + z0) / z0)
background(h) = (bg.x * profile(h), 0, bg.z * profile(h))
```

### Wind step

The Wind Solver must run the kernels below in the order given, once per Wind step.

1. **Advect.** Trace each cell center back along the velocity by `dtWind`. Sample the read copy of Velocity and Smoke at the traced point with trilinear filtering. Write the result to the write copy.
   When `advectionSharpness` is above 0, a correct kernel runs next. Decision D31 in `decisions.md` describes the correct kernel, the vorticity confinement force that follows the buoyancy, and the Turbulence write that follows the Surface Wind.
2. **Stratify.** Subtract `velocity.y * stabilityKPerM * dtWind` from `theta`.
3. **Buoyancy.** Add `g * theta / theta0 * dtWind` to `velocity.y`.
4. **Ground sources.** In each ground cell, apply the ground source equations below.
5. **Sponge.** In the relaxation zones, blend the velocity toward `background(h)` and blend `theta` toward 0.
6. **Solid.** Set the velocity of every solid cell to zero.
7. **Divergence.** Compute the divergence with central differences. Use a velocity of zero for a solid neighbor.
8. **Pressure.** Solve the pressure equation with red-black Gauss-Seidel and over-relaxation. Start from the pressure of the previous step.
9. **Project.** Subtract the pressure gradient from the velocity of every air cell.
10. **Surface Wind.** Write the Surface Wind with the equations in the Surface Wind section.

The ground source equations are below. `cell` is the cell size of 32 m.

```text
heat    = heatingGain * SurfaceHeating
        + couplingGain * fireConvFraction * 1000 * HeatFluxCoarse       (W/m2)
theta   = clamp(theta + heat * dtWind / (rhoCp * cell), thetaMin, thetaMax)
smoke   = smoke + smokeRate * HeatFluxCoarse * dtWind
speed   = |velocity|
velocity = velocity / (1 + dragCoef * speed * dtWind / cell)
```

The requirements below apply to the Wind step.

1. The advect kernel must clamp the traced point to the Wind Grid.
2. The advect kernel must multiply the smoke by `exp(-dtWind / smokeLifeS)`. When the correct kernel runs, the correct kernel does this in place of the advect kernel.
3. The sponge weight must rise linearly from 0 at the inner edge of a relaxation zone to `dtWind / spongeTimeS` at the outer face.
4. The pressure kernel must use the pressure of the cell for a solid neighbor and for a neighbor outside a lateral face or the bottom face.
5. The pressure kernel must use a pressure of 0 for a neighbor outside the top face.
6. The project kernel must not change the velocity component that points at a solid neighbor.
7. The Wind Solver must produce no NaN value and no infinite value.
8. On Scenario load, the Wind Solver must fill the velocity with `background(h)`, then run `spinUpSteps` Wind steps before the Sim Clock starts.

### Surface Wind

```text
p        = (column center X, GroundHeight + sampleHeightM, column center Z)
v        = trilinear sample of Velocity at p
n        = terrain normal from the gradient of Ground Height
vt       = v - n * dot(v, n)
scale    = ln((6.1 + z0) / z0) / ln((sampleHeightM + z0) / z0)
SurfaceWind = (vt.x, vt.z) * scale
```

### How the three wind effects arise

- **Channeling.** The solid cells block the flow, and the projection turns the flow along the canyon. A narrow section speeds the flow up.
- **Slope winds.** Surface Heating warms the ground cells on sunlit slopes. Warm air is buoyant, so the air moves up the slope. At night and in shade, the ground cells cool and the air drains down the canyon.
- **Fire coupling.** The Heat Flux Coarse warms the ground cells under the fire far more than the sun does. The plume rises and pulls air in along the ground. In a chute, the plume attaches to the slope and the inflow points upslope, the same direction as the fire. The fire then spreads faster, and the faster fire releases more heat.

**Note:** The collocated grid can show a checkerboard pattern in the pressure. A staggered grid removes the pattern. Claude Code can change to a staggered grid if the pattern is visible in the Surface Wind.

## Known Approximations

- The Wind Grid cell is 32 m. The solver does not resolve flames, eddies smaller than about 100 m, or the wind inside the fuel bed.
- The air density is constant. The solver treats buoyancy with the Boussinesq approximation, and the approximation loses accuracy for the hottest part of a plume.
- The solver has no moisture, no clouds, and no radiation. Surface Heating stands in for the surface energy balance.
- The Weather Model does not read the temperature of the Wind Grid. Air temperature and humidity come from the Scenario and the elevation only.
