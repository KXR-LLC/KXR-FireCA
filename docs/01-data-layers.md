# Data Layers

## TL;DR

- Every simulation value lives in a GPU texture on the Fire Grid, on the Wind Grid, or on the 2D footprint of the Wind Grid.
- A Scenario is a folder with three files: `elevation.r32`, `fuel.png`, and `scenario.json`.
- The Canyon Generator writes a synthetic Scenario from a seed. A real canyon will use the same three files.
- The Scenario loader validates every file and rejects a bad Scenario with a named error.

## Grids

The Fire Grid and the Wind Grid cover the same ground area. One Wind Grid column covers 16 x 16 Fire Grid cells.

| Property | Fire Grid | Wind Grid |
|---|---|---|
| Dimensions | 2D | 3D |
| Cell count (full size) | 4096 x 4096 | 256 x 128 x 256 (X, Y, Z) |
| Cell size | 2 m | 32 m |
| Extent | 8192 m x 8192 m | 8192 m x 4096 m x 8192 m |
| Cell total | 16,777,216 | 8,388,608 |

The requirements below define the grid mapping.

1. The Scenario loader must accept a Fire Grid size of 512, 1024, 2048, or 4096 cells per side.
2. The Wind Grid must have one sixteenth of the Fire Grid cell count on X and on Z.
3. The Wind Grid must have 128 cells on Y for every Fire Grid size.
4. Fire Grid cell (i, j) must cover world X from `i * 2` to `(i + 1) * 2` and world Z from `j * 2` to `(j + 1) * 2`.
5. The bottom face of the Wind Grid must sit at world Y equal to the lowest elevation in the Scenario, rounded down to a multiple of 32 m.
6. Every compute shader must read the grid sizes from uniforms. A compute shader must not contain a grid size as a literal.

The small Fire Grid sizes exist for tests. The 4096 size is the demo size.

## Texture Registry

One component, the texture registry, creates and owns every simulation texture. Other components request a texture by name. The names in the table are the normative names.

### Fire Grid textures

| Name | Format | Content | Writer |
|---|---|---|---|
| Elevation | R32_SFloat | Ground elevation, m | Scenario loader |
| Fuel | R8_UInt | Fuel code | Scenario loader |
| Slope | R16G16_SFloat | Elevation gradient (dh/dx, dh/dz), rise over run | Scenario loader |
| Normal | R16G16_SFloat | World normal X and Z. The shader rebuilds Y. | Scenario loader |
| Insolation | R16_SFloat | Direct sunlight on the surface, W/m2 | Weather Model |
| Moisture 1h | R16_SFloat | 1-hour dead fuel moisture, fraction | Weather Model |
| Spread A | R32G32B32A32_SFloat | Head rate of spread (m/s), head direction X, head direction Z, eccentricity | Spread Solver |
| Spread B | R16G16B16A16_SFloat | Reaction intensity (kW/m2), residence time (s), head flame length (m), no-wind rate of spread (m/s) | Spread Solver |
| Arrival Time | R32_SFloat, two copies | Sim Clock value at ignition, s. The value `1.0e30` means not ignited. | Spread Solver |
| Heat Flux | R16_SFloat | Heat release, kW/m2 | Spread Solver |

### Wind footprint textures

The Wind footprint is the 2D grid with the X and Z cell counts of the Wind Grid (256 x 256 at full size).

| Name | Format | Content | Writer |
|---|---|---|---|
| Ground Height | R32_SFloat | Mean elevation of the 16 x 16 Fire Grid cells under the column, m | Scenario loader |
| Surface Heating | R16_SFloat | Sensible heat flux from the sunlit ground, W/m2 | Weather Model |
| Heat Flux Coarse | R16_SFloat | Mean of the Heat Flux under the column, kW/m2 | Spread Solver |
| Surface Wind | R16G16_SFloat | Horizontal wind at 6.1 m above the ground (X, Z), m/s | Wind Solver |
| Active Mask | R8_UNorm | 1 when the 16 x 16 block, or a block next to the block, holds an active fire cell | Spread Solver |

### Wind Grid textures

| Name | Format | Content | Writer |
|---|---|---|---|
| Velocity | R16G16B16A16_SFloat, two copies | Wind X, Y, Z (m/s) and potential temperature difference (K) | Wind Solver |
| Smoke | R16_SFloat, two copies | Smoke density, unitless | Wind Solver |
| Pressure | R32_SFloat, two copies | Pressure field of the projection | Wind Solver |
| Divergence | R16_SFloat | Velocity divergence, 1/s | Wind Solver |
| Solid Mask | R8_UNorm | 1 for a cell under the terrain, 0 for an air cell | Scenario loader |
| Velocity Scratch | R32G32B32A32_SFloat | Target of the correct kernel of the advection (`decisions.md`, decision D31) | Wind Solver |
| Smoke Scratch | R16_SFloat | Target of the correct kernel of the advection | Wind Solver |
| Turbulence | R16G16B16A16_SFloat | Vorticity X, Y, Z (1/s) and turbulence speed (m/s) | Wind Solver |

The requirements below apply to every texture in the registry.

1. The texture registry must create each texture with `enableRandomWrite` set to true, except Elevation and Fuel.
2. The texture registry must use point filtering for Fuel and for Arrival Time.
3. A component must not write a texture that another component owns.
4. A two-copy texture must have a read copy and a write copy. The owner must swap the copies after each step.
5. The texture registry must report the total texture memory to the Stats HUD.

**Note:** The full-size textures use about 1.1 GB of GPU memory in total.

## Scenario Folder

A Scenario folder sits under `StreamingAssets/Scenarios/`. The folder name is the Scenario identifier.

```text
StreamingAssets/Scenarios/synthetic-canyon-01/
  elevation.r32
  fuel.png
  scenario.json
```

### elevation.r32

1. The file must contain `gridSize * gridSize` little-endian 32-bit float values and no header.
2. Each value must be a ground elevation in meters.
3. The values must be in row-major order. The first row must be the south edge (Z = 0). The first value of each row must be the west edge (X = 0).

### fuel.png

1. The file must be an 8-bit PNG with `gridSize` pixels per side.
2. The red channel of each pixel must hold the fuel code of one Fire Grid cell.
3. The top row of the image must be the north edge. The Scenario loader must flip the rows on load.
4. The Scenario loader must read the raw bytes of the image. The Scenario loader must not read the values through a color-space conversion.

### Fuel codes

The fuel codes match the LANDFIRE FBFM13 product, so a LANDFIRE raster needs no remapping.

| Code | Meaning | Burnable |
|---|---|---|
| 1 to 13 | Anderson fire behavior fuel models 1 to 13 | Yes |
| 91 | Urban | No |
| 92 | Snow or ice | No |
| 93 | Agriculture | No |
| 98 | Water | No |
| 99 | Barren | No |
| Any other value | No data | No |

### scenario.json

The example below is normative for field names, types, and units.

```json
{
  "name": "synthetic-canyon-01",
  "gridSize": 4096,
  "cellSizeM": 2.0,
  "elevationFile": "elevation.r32",
  "fuelFile": "fuel.png",
  "latitudeDeg": 37.0,
  "dayOfYear": 213,
  "startSolarHour": 13.0,
  "weather": {
    "tempMaxC": 35.0,
    "tempMinC": 16.0,
    "rhMinPct": 12.0,
    "referenceElevationM": 1200.0,
    "lapseRateCPerKm": 6.5,
    "stabilityKPerM": 0.003,
    "wind": [
      { "hour": 0.0, "speedMps": 4.0, "fromDeg": 225.0 },
      { "hour": 3.0, "speedMps": 7.0, "fromDeg": 250.0 }
    ]
  },
  "moisture": {
    "m1Pct": 5.0,
    "m10Pct": 6.0,
    "m100Pct": 8.0,
    "livePct": 80.0
  },
  "ignitions": [
    { "hour": 0.0, "xM": 4100.0, "zM": 1500.0, "radiusM": 6.0 }
  ]
}
```

| Field | Meaning |
|---|---|
| `gridSize` | Fire Grid cells per side |
| `cellSizeM` | Fire Grid cell size. The value must be 2.0. |
| `latitudeDeg`, `dayOfYear` | Inputs to the sun position |
| `startSolarHour` | Local solar time at Sim Clock zero, in hours from 0 to 24 |
| `tempMaxC`, `tempMinC` | Daily maximum and minimum air temperature at `referenceElevationM` |
| `rhMinPct` | Relative humidity at the time of `tempMaxC`, at `referenceElevationM` |
| `lapseRateCPerKm` | Decrease of air temperature with elevation |
| `stabilityKPerM` | Increase of background potential temperature with height |
| `wind[]` | Background wind at 6.1 m above flat ground. `hour` is hours from Sim Clock zero. The Weather Model interpolates between entries. |
| `moisture` | Initial fuel moisture values, in percent |
| `ignitions[]` | Ignition circles. `hour` is hours from Sim Clock zero. `xM` and `zM` are world coordinates. |
| `groundImageFile` | Optional. The file name of the Ground Image, a JPEG or PNG color image of the ground (`decisions.md`, decision D29). The example above has no Ground Image. |

### Validation

The Scenario loader must run every check below before the simulation starts. If a check fails, the Scenario loader must stop the load and show the name of the failed check in the Control Panel.

1. `gridSize` is 512, 1024, 2048, or 4096.
2. `cellSizeM` is 2.0.
3. The size of `elevation.r32` in bytes equals `gridSize * gridSize * 4`.
4. Every elevation value is finite and is between -500 and 9000.
5. The elevation range (maximum minus minimum) is less than 3000 m.
6. The width and height of `fuel.png` equal `gridSize`.
7. The `wind` array has at least one entry, and the `hour` values increase.
8. `tempMaxC` is greater than `tempMinC`.
9. Every moisture value is between 1 and 300.
10. Every ignition circle lies inside the Fire Grid.

### Derived layers

The Scenario loader must compute the layers below once, with compute shaders, after the files load.

1. Compute Slope from Elevation with central differences. Use one-sided differences on the grid edge.
2. Compute Normal from Slope.
3. Compute Ground Height as the mean of Elevation over each 16 x 16 block.
4. Compute Solid Mask: set a Wind Grid cell to 1 when the center of the cell is below Ground Height. Also set each pit cell to 1 (`decisions.md`, decision D27).
5. Keep one CPU copy of Elevation for mouse picking.

## Canyon Generator

The Canyon Generator is an editor window under `Tools > FireCA > Canyon Generator`. The Canyon Generator writes a complete Scenario folder from a settings asset and a seed.

The purpose of the synthetic canyon is to show canyon fire behavior clearly. The terrain features below each produce one visible effect: a chute shows the chimney effect, a narrow section shows wind channeling, and opposite aspects show the difference between sunlit and shaded fuels.

1. The Canyon Generator must produce the same files for the same settings and the same seed.
2. The terrain must contain one main canyon that crosses the grid from the south edge to the north edge.
3. The main canyon must have a relief of 600 m to 900 m from the floor to the rim.
4. The canyon axis must bend at least twice.
5. The main canyon must have one narrow section with a floor width under 100 m.
6. The terrain must contain at least four side drainages.
7. One side drainage must be a chute: a straight, narrow drainage with a slope between 55 percent and 100 percent, at least 600 m long, on a west-facing or south-facing wall.
8. The terrain must contain one saddle on a rim.
9. The terrain must contain fractal noise detail with an amplitude under 40 m.
10. The canyon floor must descend from the south edge to the north edge.
11. The Canyon Generator must assign fuel codes with the fuel rules table.
12. The Canyon Generator must write a `scenario.json` file with the default values from the example in this document.
13. The Canyon Generator must place the default ignition on the canyon floor, 200 m from the base of the chute.

### Fuel rules

The Canyon Generator applies the first rule that matches a cell. Relative height is 0 on the canyon floor and 1 on the rim. A sunny aspect faces between azimuth 135 and azimuth 315. A shaded aspect faces every other azimuth.

| Order | Condition | Fuel code |
|---|---|---|
| 1 | Slope over 120 percent | 99 (barren) |
| 2 | Inside the creek channel, 6 m wide, on the floor of the main canyon | 98 (water) |
| 3 | Within 40 m of the creek channel | 9 (hardwood litter) |
| 4 | Inside the chute | 4 (chaparral) |
| 5 | Relative height under 0.35, sunny aspect | 1 (short grass) |
| 6 | Relative height 0.35 to 0.85, sunny aspect | 5 (brush), with patches of 4 (chaparral) |
| 7 | Relative height under 0.85, shaded aspect | 10 (timber litter and understory), with patches of 8 (closed timber litter) |
| 8 | Relative height over 0.85 | 2 (timber grass and understory) |

The Canyon Generator must make the patches in rules 6 and 7 with a low-frequency noise field, with patch sizes between 80 m and 400 m.

## Real Canyon Import

Real canyon import is milestone M6. A Python script will convert a USGS 3DEP elevation raster and a LANDFIRE FBFM13 raster into `elevation.r32` and `fuel.png`. The Scenario format does not change.
