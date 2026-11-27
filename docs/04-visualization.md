# Visualization

## TL;DR

- One 3D view shows the canyon, the fire, the smoke, and the wind together.
- The Terrain Mesh shows fuel colors, burn scars, and fire glow. One Overlay at a time can replace the fuel colors with a data layer.
- The Smoke Pass ray-marches the Smoke texture of the Wind Grid.
- Wind Particles trace the 3D wind field. A slice plane shows a cross-section of the Wind Grid.
- The Control Panel drives the Scenario, the time, the weather, and the display. The Stats HUD shows the size and speed of the computation.

## Terrain Mesh

1. Build the Terrain Mesh on the CPU from the CPU copy of Elevation when a Scenario loads.
2. Use a vertex spacing of 8 m.
3. Split the Terrain Mesh into tiles of 128 x 128 quads.
4. Set the vertex heights from Elevation, so that shadows and culling use the true shape.
5. Do not compute vertex normals. The terrain shader reads the Normal texture at full Fire Grid resolution.

## Terrain Shader

The terrain shader is an HDRP Lit Shader Graph with one Custom Function node for the layer logic. The terrain shader composes the layers below, from bottom to top.

| Order | Layer | Source | Behavior |
|---|---|---|---|
| 1 | Fuel color | Fuel | A natural color per fuel code, with a small noise variation. The fuel color smoothing of decision D28 in `decisions.md` blends the colors of nearby cells. When the Scenario has a Ground Image, the Ground Image replaces the fuel color (decision D29). |
| 2 | Overlay | The selected Overlay | Blends over the fuel color with the Overlay opacity |
| 3 | Burn scar | Arrival Time | Darkens an ignited cell to char black, then to ash gray after `sourceHoldS` |
| 4 | Fire glow | Arrival Time, Spread B | Emissive color on flaming and smoldering cells |

The requirements below apply to the terrain shader.

1. The terrain shader must sample Fuel and Arrival Time with point filtering.
2. The terrain shader must compute the cell state from the Arrival Time and the Sim Clock, with the cell state table in `02-fire-spread.md`.
3. A flaming cell must emit with an intensity that rises with the head flame length in Spread B.
4. A smoldering cell must emit a dim red that fades to zero over `emberFadeS` seconds.
5. The default value of `emberFadeS` must be 240.
6. The fire glow must be bright enough to trigger HDRP bloom at the default exposure.

**Note:** The flaming zone of a slow fire is narrower than one cell. The ember fade keeps the fire edge visible between ignitions of adjacent cells.

## Overlays

The Control Panel selects one Overlay or none. Each scalar Overlay uses the same perceptual color map, stored as a 256 x 1 lookup texture. The Fuel model Overlay uses a categorical palette.

| Overlay | Source | Range |
|---|---|---|
| Fuel model | Fuel | Categorical, 13 colors plus gray for codes that are not burnable |
| Fuel moisture | Moisture 1h | 0 to 25 percent |
| Rate of spread | Spread A, channel 1 | 0.001 to 5 m/s, logarithmic |
| Flame length | Spread B, channel 3 | 0 to 20 m |
| Arrival time | Arrival Time | Color bands of 10 simulated minutes, with a contour line at each band edge |
| Surface wind | Surface Wind | 0 to 20 m/s, with direction arrows on a 128 m spacing |
| Air temperature | Elevation and Weather Model uniforms | `tempMinC` - 10 to `tempMaxC` + 5 |
| Relative humidity | Elevation and Weather Model uniforms | 0 to 100 percent |
| Insolation | Insolation | 0 to 1100 W/m2 |
| Slope | Slope | 0 to 120 percent |

1. The Control Panel must show a legend with the name, the unit, and the range of the selected Overlay.
2. The air temperature and relative humidity Overlays must call the same HLSL functions that the Weather Model calls.
3. The Overlay opacity must have a slider from 0 to 1, with a default of 0.8.

## Vegetation

The Vegetation draws trees, shrubs, and dead wood on the Terrain Mesh. Decision D33 in `decisions.md` holds the design, and milestone M8 in `05-milestones.md` holds the acceptance criteria.

1. Read the Plant Models from `Assets/FireCA/Models/PlantModels.bytes` and the Leaf Atlas from `Assets/FireCA/Models/LeafAtlas.png`. The script `tools/blender/plant_models.py` writes both files (decision D34).
2. Place the Plants from the Fuel layer when a Scenario loads. A grass fuel model and a fuel code that does not burn have no Plants.
3. Give each Plant a random height in the range of its Plant Model, a random width, and a random rotation.
4. In each frame, a compute kernel must pick the Plants in the view and the level of detail of each Plant.
5. Draw all Plants of one level of detail with one draw call.
6. Read the state of a Plant from the Arrival Time of the cell under the Plant: normal, burning, or burnt.
7. Draw flames and smoke on each burning Plant within 3500 m of the camera.

**Note:** The Vegetation is a display of the Fuel layer. The Spread Solver and the Wind Solver do not read the Vegetation. The model has no crown fire and no canopy sheltering (`00-overview.md`, scope).

## Smoke Pass

The Smoke Pass is an HDRP Custom Pass at the Before Post Process injection point. The Smoke Pass draws a full-screen ray march through the bounds of the Wind Grid.

1. March each view ray from the entry point of the Wind Grid bounds to the exit point, or to the scene depth, whichever is nearer.
2. Use 96 steps per ray, with a per-pixel jitter of the start point.
3. Sample the Smoke texture with trilinear filtering at each step.
4. Accumulate the transmittance with the Beer-Lambert law.
5. At each step, march 6 steps toward the sun to compute the light that reaches the smoke.
6. Add an orange glow to smoke within 96 m of a ground cell whose Heat Flux Coarse is above 50 kW/m2. Light the smoke farther away from Heat Flux Coarse as decision D30 in `decisions.md` describes.
7. Blend the result over the camera color.
8. Provide a density multiplier and an on-off toggle in the Control Panel.
9. Move the sample point of step 3 with the turbulence of decision D31 in `decisions.md`.
10. Inside the box of the Wind Nest, read the smoke of the Wind Nest, and give that part of the ray 96 steps of its own (decision D32).

The Smoke Pass can render at half resolution with a depth-aware upsample if the full-resolution cost is above 3 ms.

## Wind Particles

Wind Particles are a VFX Graph effect that samples the Velocity texture of the Wind Grid.

1. Spawn particles at random positions over the Fire Grid area.
2. Spawn each particle at a height above the ground between 10 m and 400 m, with more particles near the ground.
3. Move each particle with the Velocity sample at the position of the particle. Add the turbulence of decision D31 in `decisions.md`. Inside the box of the Wind Nest, use the Velocity of the Wind Nest (decision D32).
4. Multiply the particle motion by `particleSpeedScale`, with a default of 30. The Sim Clock time scale must not change the particle speed.
5. Draw each particle as a streak along the velocity of the particle.
6. Color each particle by wind speed with the Surface wind Overlay color map.
7. Remove a particle after 20 seconds, when the particle leaves the Wind Grid, or when the particle enters a solid cell.
8. Provide a particle count setting with a default of 2,000,000 and an on-off toggle in the Control Panel.

The speed scale exists because real wind crosses an 8 km view too slowly to read. The scale also keeps the wind visible while the Sim Clock is paused.

## Slice Plane

The slice plane is a vertical quad that cuts the Wind Grid and shows one field in color.

1. The slice plane must show wind speed, vertical velocity, `theta`, or smoke density, selected in the Control Panel.
2. The user must be able to move the slice plane along the axis of the slice plane and to switch between an east-west cut and a north-south cut.
3. The slice plane must be semi-transparent and must draw the terrain outline.

## Camera

1. Provide an orbit mode and a fly mode.
2. Provide four preset views: Overview, Canyon Mouth, Chute, and Top Down.
3. Keep the camera above the terrain surface.
4. Provide an orbit tool. The orbit tool turns the camera around the look-at point of the camera, from the position that the camera has when the tool starts. One orbit takes a set time of the Sim Clock, with a default of 6 hours (decision D35).

## Control Panel

The UI is a UI Toolkit document with three parts on the main screen: the Tool Bar at the top left, the Stats HUD at the top right, and the legend of the selected Overlay at the bottom left. Decision D35 in `decisions.md` holds the design, and milestone M9 in `05-milestones.md` holds the acceptance criteria.

The Tool Bar holds the items below.

| Item | Function |
|---|---|
| Gear button | Opens and closes the Control Panel |
| Ignite toggle | Turns the ignite tool on and off |
| Time scale group | A play and pause toggle for the Sim Clock, and two buttons that step the time scale through 1, 2, 5, 10, 30, 60, 120, 300, 600, and 1000 |
| Orbit toggle | Turns the orbit tool of the camera on and off |
| Clock | The day and the solar time of the Sim Clock |

The Control Panel is a panel under the Tool Bar. The Control Panel is closed at startup. The Control Panel has three tabs.

| Tab | Group | Controls |
|---|---|---|
| Tools | Scenario | Scenario list, Load, Reset, demo run |
| Tools | Ignition | Ignite tool switch, Clear Fire |
| Tools | Time | Sim Clock readout as day and solar time, Play, Pause, time scale slider (1 to 1000, logarithmic) |
| Tools | Camera | Orbit tool switch, time for one orbit (0.25 h to 24 h of the Sim Clock), camera presets, camera mode |
| Tools | Slice tool | Slice plane switch, field list, cut list, position |
| Weather, Wind, Fire | Weather | "Use Scenario weather" switch, background wind speed, background wind direction, `tempMaxC`, `tempMinC`, `rhMinPct` |
| Weather, Wind, Fire | Wind Solver | Advection sharpness, vorticity confinement |
| Weather, Wind, Fire | Wind Nest | Wind Nest switch, cell size, "Move the Wind Nest to the view" |
| Weather, Wind, Fire | Fire | `couplingGain` slider (0 to 2), live fuel moisture |
| Display | Overlay | Overlay list, Overlay opacity, fuel color smoothing, Ground Image opacity |
| Display | Vegetation | Vegetation switch, density, draw distance, effects switch, shadow switch |
| Display | Smoke | Smoke switch, density, darkness, billows, turbulence |
| Display | Wind Particles | Wind Particles switch, count, size |

The UI does not use the default theme of Unity. The style sheet `Assets/FireCA/UI/FireCA.uss` and the custom controls in `Runtime/UI/UiControls.cs` draw every control. The typeface is B612.

1. A manual weather value must override the Scenario value until the user turns "Use Scenario weather" on.
2. Reset must reload the Scenario and set the Sim Clock to zero.
3. Clear Fire must set the Arrival Time of every cell to `1.0e30` and must clear the Smoke texture.
4. While the ignite tool is on, a click on the terrain must ignite the cells at the hit point.

## Stats HUD

The Stats HUD is a compact panel in the top right corner. The Stats HUD updates four times per second. The Stats HUD has two tabs: Conditions and Performance. The Conditions tab is on show at startup.

The Conditions tab has the readouts below.

| Readout | Content |
|---|---|
| Scenario | Id, extent, and elevation range |
| Background wind | Speed, direction, and a dial with an arrow that points the way the air moves |
| Sim Clock | Day and solar time |
| Elapsed | Hours and minutes from the start of the Scenario |
| Time scale | Requested value and achieved value |
| Air temperature | Degrees Celsius at the reference elevation |
| Relative humidity | Percent at the reference elevation |
| Sun elevation | Degrees |
| Fuel moisture | 10-h, 100-h, and live values, in percent |
| Area burning | Hectares, from a GPU count of flaming cells |
| Area burnt | Hectares, from a GPU count of ignited cells |
| Burning Plants in view | Count of burning Plants that the Vegetation draws |

The Performance tab has the readouts below.

| Readout | Content |
|---|---|
| Fire Grid | Cell count |
| Wind Grid | Cell count |
| Plants | Count of Plants drawn, count of Plants in the Scenario, and count of burning Plants |
| Active blocks | Count of blocks with the Active Mask set |
| Burned area | Hectares, from a GPU count of ignited cells |
| Fire steps | Steps per second and GPU milliseconds per step |
| Wind steps | Steps per second and GPU milliseconds per step |
| Cell updates | Fire and wind cell updates per second, in billions |
| Time scale | Requested value and achieved value |
| Frame | Frames per second and milliseconds |
| Texture memory | Megabytes, from the texture registry |

The cell updates readout is the headline number for the KXR team. The value counts each Fire Grid cell once per Fire step and each Wind Grid cell once per pressure iteration.
