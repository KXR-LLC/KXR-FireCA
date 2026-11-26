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

## Control Panel

The Control Panel is a UI Toolkit panel on the left edge of the screen. The user can collapse the Control Panel.

| Section | Controls |
|---|---|
| Scenario | Scenario list, Load, Reset |
| Time | Play, Pause, time scale slider (1 to 1000, logarithmic), Sim Clock readout as day and solar time |
| Ignition | Ignite tool toggle, Clear Fire |
| Weather | Background wind speed, background wind direction, `tempMaxC`, `tempMinC`, `rhMinPct`, "Use Scenario weather" toggle |
| Fire | `couplingGain` slider (0 to 2), live fuel moisture |
| Display | Overlay list, Overlay opacity, smoke toggle and density, Wind Particles toggle and count, slice plane controls, camera presets |

1. A manual weather value must override the Scenario value until the user turns "Use Scenario weather" on.
2. Reset must reload the Scenario and set the Sim Clock to zero.
3. Clear Fire must set the Arrival Time of every cell to `1.0e30` and must clear the Smoke texture.
4. While the ignite tool is on, a click on the terrain must ignite the cells at the hit point.

## Stats HUD

The Stats HUD is a compact panel in the top right corner. The Stats HUD updates four times per second.

| Readout | Content |
|---|---|
| Fire Grid | Cell count |
| Wind Grid | Cell count |
| Active blocks | Count of blocks with the Active Mask set |
| Burned area | Hectares, from a GPU count of ignited cells |
| Fire steps | Steps per second and GPU milliseconds per step |
| Wind steps | Steps per second and GPU milliseconds per step |
| Cell updates | Fire and wind cell updates per second, in billions |
| Time scale | Requested value and achieved value |
| Frame | Frames per second and milliseconds |
| Texture memory | Megabytes, from the texture registry |

The cell updates readout is the headline number for the KXR team. The value counts each Fire Grid cell once per Fire step and each Wind Grid cell once per pressure iteration.
