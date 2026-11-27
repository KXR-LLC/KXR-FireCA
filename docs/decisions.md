# Decisions

## TL;DR

- This file records each spec gap that blocked the work, and the answer that Claude Code chose.
- Each decision has an identifier, the milestone, the question, the answer, and the reason.
- A decision stays in force until a later decision replaces the decision.

## Decision List

| ID | Milestone | Subject |
|---|---|---|
| D1 | M0 | Unity editor version |
| D2 | M0 | Shape of a synthetic canyon on a small Fire Grid |
| D3 | M0 | Source of the camera presets |
| D4 | M0 | Format of the Fuel texture |
| D5 | M0 | Aspect of flat ground in the fuel rules |
| D6 | M0 | Color type of `fuel.png` |
| D7 | M0 | Creation of the simulation textures |
| D8 | M0 | Measurement of the frame rate |
| D9 | M0 | Position of the default ignition |
| D10 | M1 | Active Mask hold time, heat of a burned-out cell, and the Block Stats texture |
| D11 | M1 | Burn scar under the Arrival time Overlay |
| D12 | M1 | Fire time and the Sim Clock |
| D13 | M1 | Moisture damping at the moisture of extinction |
| D14 | M1 | Writers of Moisture 1h and Surface Wind before M2 and M3 |
| D15 | M1 | Heat Flux Coarse |
| D16 | M2 | Check of the fuel moisture constants against the publication |
| D17 | M2 | The sun light |
| D18 | M2 | Manual weather and the moisture update |
| D19 | M3 | Staggered Wind Grid |
| D20 | M3 | Format of the Velocity texture |
| D21 | M3 | Wind Particles without VFX Graph |
| D22 | M3 | Spin-up, the frame loop, and the Wind Solver switch |
| D23 | M4 | Fire coupling, the smoke source, and the Smoke Pass |
| D24 | M4 | Reading of the inflow criterion |
| D25 | M5 | The demo run and the benchmark |
| D26 | M6 | The real canyon import |

## D1: Unity editor version

**Question.** `00-overview.md` fixes the engine as Unity 6 LTS. Six Unity 6 editors are installed. Which editor does the project use?

**Answer.** The project uses Unity 6000.3.16f1 with HDRP 17.3.0.

**Reason.** Unity 6.3 is the only LTS stream among the installed editors. The versions 6.4, 6.5, and 6.6 are update releases. 6000.3.16f1 is the newest installed patch of the LTS stream.

## D2: Shape of a synthetic canyon on a small Fire Grid

**Question.** The Canyon Generator requirements give sizes in meters (a relief of 600 m to 900 m, a chute of 600 m). A 512 Fire Grid covers 1024 m per side. The full-size canyon does not fit. What does the Canyon Generator write for `test-canyon-512`?

**Answer.** The Canyon Generator defines the canyon on the full-size grid of 8192 m. For a smaller Fire Grid, the Canyon Generator scales every horizontal length and every height by `gridSize / 4096`. Heights scale about the elevation 1200 m. Two values do not scale: the creek channel is 6 m wide at every size, and the ignition radius is 6 m at every size.

**Reason.** A uniform scale keeps every slope, so the fire behavior per cell on the test grid matches the full-size grid. The creek channel keeps 3 cells of width, so the creek channel stays a barrier for the 24-neighbor arrival pass.

**Effect.** Canyon Generator requirements 2 to 10 hold on `synthetic-canyon-01` only. The editor tests measure the requirements on `synthetic-canyon-01`.

## D3: Source of the camera presets

**Question.** `04-visualization.md` names four preset views: Overview, Canyon Mouth, Chute, and Top Down. The Scenario format has no field for the position of the chute. Where do the preset positions come from?

**Answer.** `scenario.json` can hold an optional `cameraPresets` array. Each entry has a `name`, a `positionM` vector, and a `lookAtM` vector, in world meters. The Canyon Generator writes the four entries. When a Scenario has no entry for a preset, the camera computes a fallback from the elevation and from the first ignition.

**Reason.** The Canyon Generator knows the canyon layout, and the Scenario loader does not. An optional field keeps a Scenario without the field valid, so the real canyon import of M6 needs no change.

**Addition at M1.** `scenario.json` can also hold an optional `landmarks` array. Each entry has a `name`, an `xM` value, and a `zM` value, in world meters. The Canyon Generator writes the landmarks `chuteBase` and `chuteTop`. The chute test of M1 acceptance criterion 5 reads the two landmarks, and the chute test of M4 will read the same landmarks.

## D4: Format of the Fuel texture

**Question.** `01-data-layers.md` gives the Fuel texture the format `R8_UInt`, with no random write. Unity 6000.3 on Direct3D 12 cannot create a `Texture2D` with the format `R8_UInt`. Which format does the Fuel texture use?

**Answer.** The Fuel texture uses the format `R8_UNorm` with point filtering. A shader reads the fuel code with `FireCA_LoadFuelCode` in `FireCACommon.hlsl`. The function returns the stored byte as an unsigned integer.

**Reason.** `R8_UNorm` stores the same byte, and the conversion `value * 255 + 0.5` returns the exact code. The other option was a `RenderTexture` with random write and a copy kernel. That option breaks the rule that Fuel has no random write.

## D5: Aspect of flat ground in the fuel rules

**Question.** The fuel rules split cells by sunny aspect and shaded aspect. The canyon floor is flat, with a fall of under 2 percent toward the north. The literal rule makes the floor a shaded aspect, and the floor gets fuel 10 (timber litter and understory). Is that the intent?

**Answer.** A cell whose noise-free surface has a slope under 5 percent has no aspect. The fuel rules treat the cell as a sunny aspect. The canyon floor outside the riparian strip gets fuel 1 (short grass).

**Reason.** Flat ground receives full sun. The demo script in `05-milestones.md` expects a fire on the floor that grows as an ellipse and reaches the chute in about 40 simulated minutes. Short grass gives that behavior. Timber litter does not. This decision changes a Scenario input. This decision does not change an equation or a Fuel Table value.

## D6: Color type of fuel.png

**Question.** `01-data-layers.md` states that the red channel of `fuel.png` holds the fuel code. Which PNG color type does the Canyon Generator write, and which color types does the Scenario loader accept?

**Answer.** The Canyon Generator writes an 8-bit grayscale PNG. The Scenario loader accepts an 8-bit PNG that is grayscale, grayscale with alpha, RGB, RGBA, or palette. The Scenario loader reads the gray value of a grayscale image as the red channel. The Scenario loader rejects a 16-bit PNG and an interlaced PNG.

**Reason.** A grayscale file is smaller, and an image viewer shows the same value in the red channel. The project has its own PNG reader (`PngCodec`), so the Scenario loader reads the raw bytes with no color-space conversion. The same writer gives the Canyon Generator identical files on every run.

## D7: Creation of the simulation textures

**Question.** M0 uses six of the simulation textures. Does the texture registry create the other textures at M0?

**Answer.** The texture registry creates every texture of `01-data-layers.md` when a Scenario loads.

**Reason.** The texture memory readout of the Stats HUD then shows the true total from M0 (1065 MB at the 4096 size). Later milestones add no texture and change no registry code.

## D8: Measurement of the frame rate

**Question.** M0 acceptance criterion 5 requires 60 frames per second at 2560 x 1440 from every camera preset. How is the frame rate measured?

**Answer.** The `FrameBenchmark` component measures the frame rate in a standalone Windows player. The player starts with `-firecaBench <report path>`. For each camera preset, the benchmark waits 2 seconds, then records every frame for 5 seconds. The benchmark turns vertical sync off. The report holds the mean frame rate, the 1 percent low frame rate, and the lowest frame rate of each preset.

**Reason.** The Game view of the editor adds editor overhead and does not run at a fixed 2560 x 1440. The standalone player is the form that the KXR team will see.

## D9: Position of the default ignition

**Question.** Canyon Generator requirement 12 takes the default values from the example `scenario.json`, which has an ignition at (4100, 1500). Requirement 13 places the default ignition 200 m from the base of the chute. Which requirement sets the ignition?

**Answer.** Requirement 13 sets the ignition. The Canyon Generator writes one ignition on the canyon floor, 200 m from the base of the chute, on the chute side of the creek channel. Every other value comes from the example.

**Reason.** The creek channel is a barrier. An ignition on the far side of the creek channel cannot reach the chute.

## D10: Active Mask hold time, heat of a burned-out cell, and the Block Stats texture

**Question.** `02-fire-spread.md` skips the heat flux pass on a block that is not in the Active Mask. A block leaves the Active Mask when its last cell passes `sourceHoldS`. At that time the smolder term of the heat flux equation is still above 1 kW/m2 for a heavy fuel, so the Heat Flux of the block keeps a stale value. The Active Mask update also needs a per-block flag and the largest rate of spread per block, and `01-data-layers.md` has no texture for the values. What does the Spread Solver do?

**Answer.** The Spread Solver makes three choices.

1. The heat flux pass writes 0 for a cell that is burned out (`now - arrival` is at least `sourceHoldS`).
2. A cell keeps its block in the Active Mask for `sourceHoldS + 60` seconds. The 60 seconds are two Fire steps of the largest size. A cell stops being a source at `sourceHoldS`, as the document states.
3. The texture registry holds one more texture, Block Stats (`R32G32B32A32_SFloat` on the Wind footprint). The channels hold the block-active flag, the largest head rate of spread, the count of ignited cells, and the count of flaming cells.

**Reason.** Choices 1 and 2 together make sure that the heat flux pass writes 0 for every cell of a block before the block leaves the Active Mask. The largest smolder value that choice 1 removes is about 7 kW/m2. Block Stats lets the Active Mask update scan each block once, and lets one small readback return the active block count, the value `rMax`, and the burned area.

## D11: Burn scar under the Arrival time Overlay

**Question.** `04-visualization.md` draws the burn scar layer above the Overlay layer. The burn scar is char black, so the burn scar hides the Arrival time Overlay on every ignited cell. The demo run of M5 shows the Arrival time Overlay over the whole burn. Which layer wins?

**Answer.** While the Arrival time Overlay is selected, the terrain shader multiplies the burn scar of an ignited cell by `1 - opacity`, where `opacity` is the Overlay opacity. Every other Overlay keeps the layer order of the document.

**Reason.** The Arrival time Overlay has data on ignited cells only. The other Overlays have data on every cell, so the burn scar on top still reads correctly.

## D12: Fire time and the Sim Clock

**Question.** The frame loop runs Fire steps until the fire time reaches the Sim Clock target. A Fire step can be 30 seconds long, and a frame at a low time scale advances the Sim Clock by a few milliseconds. Does a Fire step shrink to the frame, or does the fire time differ from the Sim Clock?

**Answer.** A Fire step always has the size that `rMax` gives. The fire time can lead the Sim Clock by up to one Fire step. The arrival pass writes arrival times up to the fire time. The terrain shader treats a cell whose Arrival Time is later than the Sim Clock as unburned. The burned area readout of the Stats HUD counts every ignited cell, so the readout can lead the picture by one Fire step. The Sim Clock starts paused after a Scenario load, and a Scenario ignition at hour 0 runs at once.

**Reason.** The fire front on screen then moves smoothly at any time scale, and the count of Fire steps does not grow with the frame rate. A shrunken Fire step would run one tiny step in each frame.

**Effect.** After an ignition the Spread Solver waits for one GPU readback, so the first Fire step has a valid size. Every other Fire step uses the newest value of `rMax` that is available.

## D13: Moisture damping at the moisture of extinction

**Question.** The EditMode test requires a head rate of spread of exactly 0 when the dead fuel moisture equals `mxDead`. The damping polynomial gives about 4e-16 at a ratio of 1 in 64-bit arithmetic, and the Python oracle returns 1.6e-17 m/s. How does the Reference Model return 0?

**Answer.** The Reference Model returns a damping value of 0 when the moisture ratio is at least `1 - 1e-12`, and clamps the polynomial to 0 from below. The behavior pass on the GPU clamps the polynomial to 0 from below.

**Reason.** The guard changes no value above the rounding error of the polynomial. Every Reference Case still matches within 0.1 percent.

## D14: Writers of Moisture 1h and Surface Wind before M2 and M3

**Question.** The Weather Model owns Moisture 1h, and the Wind Solver owns the Surface Wind. M1 needs both textures, and neither system exists at M1. Which component writes the textures?

**Answer.** The Weather Model exists from M1 in a small form. The Weather Model holds the four fuel moisture values of the Scenario, fills Moisture 1h with the 1-hour value, interpolates the background wind once per 60 simulated seconds, and fills the Surface Wind with the background wind. M2 replaces the moisture fill. M3 moves the Surface Wind to the Wind Solver.

**Reason.** The Spread Solver then reads its inputs from the same textures at every milestone, and the Spread Solver code does not change at M2 or at M3.

## D15: Heat Flux Coarse

**Question.** `02-fire-spread.md` writes Heat Flux Coarse after the heat flux pass. `05-milestones.md` lists Heat Flux Coarse in the build list of M4. Which milestone builds Heat Flux Coarse?

**Answer.** M4 builds Heat Flux Coarse. At M1 the texture exists in the texture registry and holds no data.

**Reason.** The milestone list sets the build order, and no M1 system reads Heat Flux Coarse.

## D16: Check of the fuel moisture constants against the publication

**Question.** `03-weather-wind.md` asks for a check of the fuel-level temperature, fuel-level humidity, and EMC constants against Rothermel and others (1986), Research Paper INT-359, before M2 closes. What is the result?

**Answer.** The check has two results.

1. The fuel-level equations match the publication. Page 9 of INT-359 prints equation 1, `Tf - Ta = I / (0.015 Uh + 0.026)`, and equation 2, `Hf = Ha exp(-0.033 (Tf - Ta))`. The temperatures are in deg F, `I` is in cal/cm2/min, and `Uh` is the wind at fuel level in mi/h. The constants and the units in `03-weather-wind.md` are the same.
2. The three EMC equations are not in INT-359. INT-359 passes the fuel-level temperature and humidity to the Canadian Fine Fuel Moisture Code. The three equations in `03-weather-wind.md` have the form of the Simard (1968) equations that the National Fire Danger Rating System uses. Claude Code did not find a copy of that source to check the six constants against.

**Effect.** The Weather Model uses the EMC equations as `03-weather-wind.md` states them. The source note in `03-weather-wind.md` names the wrong publication for the EMC equations. A person with the NFDRS documentation should check the six EMC constants.

**Reason.** The equations in the documents are normative, and the results are plausible: Moisture 1h at the reference elevation ranges from 2.0 percent in the afternoon to 6.9 percent before dawn.

## D17: The sun light

**Question.** `03-weather-wind.md` sets the HDRP directional light to the sun direction at each Insolation recompute. The document does not state the light intensity or the behavior at night. What does the Weather Model do?

**Answer.** At each Insolation recompute, the Weather Model points the directional light along the sun direction and sets the light intensity to the scene value multiplied by `min(1, dni / 1000)`. The intensity is 0 when the sun is under 1.1 degrees of elevation. The light keeps a small downward angle at night.

**Reason.** The light then fades at sunset with the same `dni` value that Insolation uses, and the fire glow is the main light at night.

## D18: Manual weather and the moisture update

**Question.** The Control Panel has a "Use Scenario weather" toggle and five weather controls. How do the controls and the 60-second update interval work together?

**Answer.** A change of a weather control makes all five values manual and turns the toggle off. The manual values apply in the next frame, with no wait for the 60-second interval, and the change does not advance the fuel moisture. Turning the toggle on restores the Scenario values. Turning the toggle off freezes the current values as manual values. The Weather Model keeps `tempMinC` at least 0.5 deg C under `tempMaxC`.

**Reason.** A slider must give a visible response at once. Validation check 8 requires `tempMaxC` greater than `tempMinC`.

**Note.** Moisture 1h is a 16-bit float texture. Near equilibrium the per-step change can fall under the precision of the format, so Moisture 1h can stop up to 0.1 percent of moisture short of the EMC. The GPU value matched the CPU equations within 0.005 percent of moisture after one update.

## D19: Staggered Wind Grid

**Question.** `03-weather-wind.md` describes a collocated grid, and notes that Claude Code can change to a staggered grid if a checkerboard pattern is visible. M3 acceptance criterion 1 requires the project kernel to remove 90 percent of the divergence. On a collocated grid the central-difference divergence and the 7-point pressure equation do not match, so a converged pressure does not remove the measured divergence. Which grid does the Wind Solver use?

**Answer.** The Wind Solver uses a staggered grid from the start. One Velocity texel holds wind X on the west face of the cell, wind Y on the bottom face, wind Z on the south face, and `theta` at the cell center. The texture layout and the texture names do not change. The choices below follow from the staggered grid.

1. A face between an air cell and a solid cell carries no flow. The solid kernel and the project kernel hold the face at zero.
2. The advect kernel traces each face center and the cell center back along the velocity. A reader of Velocity samples each component with a half-cell offset.
3. Steps 2 to 6 of the Wind step (stratify, buoyancy, ground sources, sponge, solid) run in one kernel, in the order of the document. Each step acts on one cell, so the result is the same.
4. The texture registry holds one more texture, Wind Flags (`R8_UNorm` on the Wind Grid). Each air cell stores which of its six neighbors take part in the pressure equation. The pressure kernel then reads no Solid Mask value.
5. The pressure kernel updates the Pressure texture in place, red cells and then black cells. The second copy of Pressure is not used.

**Reason.** On the staggered grid the divergence, the pressure equation, and the pressure gradient match exactly, so the projection is exact to the accuracy of the pressure solve. The terrain boundary is also exact, which helps the channeling criteria.

## D20: Format of the Velocity texture

**Question.** `01-data-layers.md` gives Velocity the format `R16G16B16A16_SFloat`. With that format the project kernel left 43 percent of the divergence in a steady flow. Why, and what is the fix?

**Answer.** Velocity uses the format `R32G32B32A32_SFloat`. The texture memory at the 4096 size rises from 1066 MB to 1202 MB.

**Reason.** A 16-bit float holds a wind of 12 m/s to about 0.004 m/s. The rounding of each write adds a divergence of about 1e-4 1/s, which is larger than the divergence that one Wind step of a steady flow creates (7e-5 1/s). With the 32-bit format the project kernel leaves 3.2 percent of the divergence. The Wind step takes 4.9 ms with the 32-bit format and took 4.4 ms with the 16-bit format.

**Note.** Criterion 1 holds for a spun-up flow. In the first Wind steps after a Scenario load, 30 pressure iterations from a zero pressure do not converge, and the mean absolute divergence rises. The spin-up of 300 Wind steps exists for this reason.

## D21: Wind Particles without VFX Graph

**Question.** `04-visualization.md` builds Wind Particles as a VFX Graph effect. Claude Code works through files and the command line, and a VFX Graph asset can only be authored in the graph editor. How are Wind Particles built?

**Answer.** Wind Particles are a compute kernel and a procedural draw. `WindParticles.compute` moves the particles in a structured buffer. `WindParticles.shader` draws each particle as a line streak in an HDRP forward pass. The effect meets requirements 1 to 8 of the Wind Particles section: random spawn over the Fire Grid, more particles near the ground between 10 m and 400 m, motion with the Velocity sample, `particleSpeedScale` of 30 in real time, a streak along the velocity, the Surface wind color map, removal after 20 seconds or outside the Wind Grid or inside a solid cell, and a count setting with a default of 2,000,000.

**Reason.** The result on screen is the same, and the code path also shows compute shaders at work, which is the purpose of the project. A person with the Unity editor can replace the effect with a VFX Graph asset later. The same holds for the slice plane, which uses a hand-written HDRP shader.

## D22: Spin-up, the frame loop, and the Wind Solver switch

**Question.** The Wind Solver runs `spinUpSteps` Wind steps before the Sim Clock starts. 300 Wind steps take about 1.5 seconds at the 4096 size. M0 acceptance criterion 1 limits the Scenario load, and M3 asks for a progress readout. When does the spin-up run, and what happens to the tests of M1 and M2 that use a uniform wind?

**Answer.**

1. The Scenario load fills the velocity with `background(h)` and returns. The Step Scheduler then runs 6 spin-up Wind steps per frame. Until the spin-up ends, the Sim Clock does not advance, Play is disabled, the Scenario ignitions wait, and the Control Panel and the Stats HUD show the spin-up progress.
2. The spin-up does not advance the wind time. After the spin-up, a Wind step always has the size `dtWind`, and the wind time can lead the Sim Clock by less than `dtWind`. The fire time works the same way (decision D12).
3. `FireCAApp.WindSolverEnabled` turns the Wind Solver off for the next Scenario load. The Weather Model then fills the Surface Wind with the background wind, as in M1 and M2. The PlayMode tests of M1 and M2 run with the Wind Solver off, because the tests state a uniform wind.

**Reason.** The picture stays live during the spin-up, and the load stays far under 10 seconds.

**Results of M3.** The tunables `heatingGain` (1.0) and `dragCoef` (0.02) keep the default values. Criteria 5 and 6 passed with the defaults: 92.8 percent upslope and 94.5 percent downslope. The channeling test measures the mean angle between the Surface Wind and the canyon axis as an undirected angle, because an axis has no sign. The result is 15.5 degrees, and the flow on the canyon floor runs north (mean along-axis cosine 0.96). The mean Surface Wind speed on the canyon floor is 1.6 m/s under a background wind of 4 m/s, so a fire on the canyon floor is slower with the Wind Solver than with the uniform wind of M1.

## D23: Fire coupling, the smoke source, and the Smoke Pass

**Question.** M4 builds the fire term of the ground sources, the smoke source, and the Smoke Pass. Several details are open: the kernel that adds the smoke, the lighting of the smoke, and the size of the test grid. What does M4 choose?

**Answer.**

1. The Spread Solver writes Heat Flux Coarse after the heat flux pass of each Fire step. The value of a block that is not in the Active Mask is 0.
2. The advect kernel adds the smoke source to the ground cell, after the advection and the decay of the smoke. The forces kernel adds the fire heat to the ground cell.
3. The Smoke Pass renders at full resolution. The extinction is 0.012 per meter for a smoke density of 1, multiplied by the density control of the Control Panel (0 to 4). The sun term uses the color and the intensity of the directional light. The sky term is a fixed ambient value that falls at night.
4. The Control Panel has a Fire section with `couplingGain` (0 to 2) and the live fuel moisture. `couplingGain` has a default of 1.0 and applies to every loaded Scenario.
5. Clear Fire clears both copies of the Smoke texture.
6. The plume test and the inflow test use a Fire Grid of 1024 cells per side. On a 512 Fire Grid the point 200 m outside the disc edge lies inside the lateral relaxation zone of the Wind Grid.

**Results of M4.** On `synthetic-canyon-01` with the Wind Solver on, the fire from the default ignition reaches the top of the chute at 48.5 simulated minutes with `couplingGain` 1 and at 62.0 minutes with `couplingGain` 0. The fire enters the chute at 31.0 minutes and at 33.5 minutes. The mean Surface Wind in the middle of the chute, while the fire climbs, is 1.63 m/s upslope with coupling and 0.18 m/s upslope without coupling. The coupled run of 6 simulated hours has no NaN value. `theta` stays between -2.6 K and 300 K, and the peak wind speed is 22 m/s.

## D24: Reading of the inflow criterion

**Question.** M4 acceptance criterion 2 requires that, in the plume run, the Surface Wind 200 m outside the disc edge points toward the disc center with a speed of at least 0.5 m/s. The criterion does not state the time of the measurement. What does the test measure?

**Answer.** The test samples the run once per 10 Wind steps. At each sample the test takes the lowest inward component of the Surface Wind over 8 points on the circle. The test passes when at least 3 samples have an inward component of 0.5 m/s or more at all 8 points while the vertical velocity above the disc is over 5 m/s.

**Result.** The inward component is 1.2 m/s at Wind step 20 and stays over 0.5 m/s until Wind step 60. The inward component then falls: 0.24 m/s at Wind step 100 and 0.06 m/s at Wind step 300. The plume stays strong for the whole run (45 m/s to 85 m/s of vertical velocity).

**Reason and open point.** The inflow is real while the whole disc flames. After that, the plume narrows to a core a few cells wide. A narrow core carries a small mass flow, so the inflow at 350 m from the center is weak, and the Surface Wind sample at 48 m above the ground misses the inflow in the ground cell. A run on a Wind Grid twice as wide gave the same result, so the lateral boundary is not the cause. The test therefore does not show a lasting inflow. A person should decide whether the criterion means a lasting inflow. If so, the fix belongs to the Wind Solver (entrainment at the 32 m cell size), not to `couplingGain`.

**Note.** The vertical velocity of the disc plume is high because the heat flux equation gives a burning disc of fuel 4 a smolder heat of over 40 kW/m2 for 10 minutes on every cell, and `theta` reaches 50 K in a column 4 km tall. The values are not validated against a measured plume.

## D25: The demo run and the benchmark

**Question.** M5 builds a scripted demo run that starts with one key, and measures six performance targets. The key, the place of the Canyon Mouth view, the content of beat 4, and the method of the measurement are open. What does M5 choose?

**Answer.**

1. The F5 key starts and stops the demo run. The Control Panel has a button for the same action. The demo run reloads the Scenario, waits for the Wind Solver spin-up, and then runs the four beats of `05-milestones.md` by the Sim Clock.
2. Beat 1 has no Sim Clock time, so beat 1 lasts 14 seconds of real time.
3. The Canyon Mouth preset of `synthetic-canyon-01` is inside the canyon, 1500 m south of the default ignition, 430 m above the floor, and looks north. From the north end of the canyon, the narrow section hides the ignition.
4. Beat 4 hides the Wind Particles and the smoke. From the Top Down view, both cover the Arrival time bands. The end of the demo run and the stop of the demo run show both again.
5. The benchmark runs in the standalone player with `-firecaBench`. The benchmark burns the fire for 45 simulated minutes, then measures the frame rate for 10 seconds at 60x and for 10 seconds at 240x. The benchmark times each compute pass with a wall clock around 40 runs, with a wait for the GPU before and after. The Smoke Pass value is the difference of the frame time with the smoke on and with the smoke off.
6. The cell updates readout counts 16,777,216 cells per Fire step and 8,388,608 cells per pressure iteration, as `04-visualization.md` states. A Fire step computes the cells of the active blocks only, so the readout is the size of the grids that the steps cover, not the count of cells that changed.

**Reason.** Each choice keeps the beat table of the milestone document and makes the beat show what the table says the viewer sees.

**Results of M5.** Every performance target is met with the default settings. `docs/demo-notes.md` holds the numbers.

## D26: The real canyon import

**Question.** `05-milestones.md` states that `tools/import_canyon.py` reads the two rasters with GDAL. The build machine has no GDAL. The data folder `data/south-canyon/` holds a 3DEP elevation raster and a LANDFIRE FBFM13 raster that are already cut to the Fire Grid, and a `scenario.json` with the weather of July 6, 1994. How does the script read the rasters, and where do the Scenario settings come from?

**Answer.**

1. The script has two readers. The GDAL reader warps a raster from any coordinate system to the Fire Grid. The tifffile reader works when GDAL is not installed. The tifffile reader needs both rasters in one projected coordinate system in meters, and reads the georeference from the GeoTIFF tags or from a world file. The script picks the GDAL reader when the `osgeo` package can be imported.
2. Both readers resample with bilinear filtering for elevation and nearest neighbor for fuel. When a raster is on the Fire Grid already, the tifffile reader cuts the raster and changes no value.
3. The script computes the latitude of the center of the Fire Grid from the coordinate system. The tifffile reader supports UTM coordinate systems for the latitude. The `--latitude` option overrides the value.
4. The `--settings` option takes a `scenario.json` file. The script keeps the date, the weather, the moisture, and the ignitions of the file. Without the option, the script writes the default values of `01-data-layers.md` and no ignition.
5. The script runs the validation checks of the Scenario loader before the script writes a file.
6. A fuel value outside 0 to 255 becomes code 0, which the Scenario loader treats as no data.

**Results of M6.** The Scenario `south-canyon` came from the script with the tifffile reader. `elevation.r32` is identical to the prepared file in `data/south-canyon/scenario/`, and every pixel of `fuel.png` is the same. The computed latitude is 39.578, the value in the data notes. The Scenario passes every validation check and loads in 0.22 s. The fire, the weather, and the wind ran for 5 simulated hours with no code change and no NaN value.

**Open points.**

- The GDAL reader has not run, because the machine has no GDAL. A person with GDAL should run the script once with `--reader gdal`.
- The simulated fire burns 9 hectares in 5 hours. The 1994 fire burned 890 hectares. The data notes give the cause: the LANDFIRE fuels describe the vegetation of 2025, after two fires. The ignition point lies in fuel 8 (closed timber litter), which spreads at under 2 chains per hour. The Scenario shows that the systems run on real data. The Scenario does not reproduce the 1994 fire. A fuel layer for 1994 needs a repaint of `fuel.png` from the report in `data/south-canyon/reference/`.
- The camera presets of `south-canyon` come from the fallback of decision D3, because the Scenario has no `cameraPresets` entry.

## D27: Pit cells in the Solid Mask

**Question.** On `south-canyon`, the Wind Particles showed about 20 thin vertical streams at fixed places on the terrain, with no fire under them. A probe of the Wind Grid found columns one cell wide with a vertical velocity of 20 m/s to 70 m/s and a `theta` near 100 K. Each column stood on a pit cell: an air cell with a solid cell on all four sides. What is the cause, and what does the Scenario loader do about a pit cell?

**Answer.** The Scenario loader sets each pit cell of the Solid Mask to 1. The solid height of a column is the larger of two values: the count of cells with the center below Ground Height, and the lowest such count of the four columns beside the column. A column outside the Wind Grid counts as open air. The Ground Height texture does not change.

**Reason.** A pit cell has one open face, the top face. An incompressible flow cannot pass through a cell with one open face, so the velocity of a pit cell stays 0. The ground heat source adds heat to the pit cell on every Wind step, and no flow carries the heat away. With the sun alone, a pit cell gains about 0.015 K per Wind step and reaches 100 K in one afternoon. The advect kernel of the cells above reads `theta` from the pit cell and never lowers the value in the pit cell, so the pit cell feeds a hot jet without end. The fault is in the grid, not in a tunable. A pit that is one cell wide is smaller than the Wind Grid can resolve. One pass over the columns removes every pit cell, because a filled column never rises above the lowest column beside it.

**Results.** `south-canyon` has no pit cell after the change. In one simulated hour of sun with no fire, the highest `theta` stays near 8 K and does not grow. Before the change the same run failed the test. At 16:48 solar time with the Scenario weather, the largest vertical velocity away from the fire is 13 m/s, in air with a `theta` of 0 that the wind lifts over a ridge. `synthetic-canyon-01` and `test-canyon-512` pass every M3 and M4 test with the change.

**Open point.** A cell with three solid sides still has a weak flow and collects some heat (near 10 K in the test). The plume of a real fire still rises as a narrow column. Decision D24 describes that limit.

## D28: Fuel color smoothing, particle size, and smoke look

**Question.** On `south-canyon`, the fuel color layer shows the 30 m source cells of the LANDFIRE raster as squares. The Wind Particles have one width of 1 pixel. The smoke has one look: thin and white. `04-visualization.md` defines none of the controls that change these. What does the Control Panel add?

**Answer.** The Display section has five more controls. Each control changes the picture only. No control changes a simulation texture.

| Control | Range | Default | Effect |
|---|---|---|---|
| Fuel color smoothing (m) | 0 to 60 | 24 | The fuel color of a pixel is the weighted mean fuel color of 25 Fire Grid cells around the pixel, on a 5 x 5 pattern with a spacing of half the value. A noise field moves the pattern by up to 1.2 times the value, so a straight edge between two fuels becomes an irregular edge. The value 0 shows the fuel color of each cell. |
| Particle size (m) | 0 to 40 | 0 | The width of one Wind Particle streak. At 0, a streak is 1 pixel wide. A wide streak has soft sides and a round head, and is at least 1.5 times as long as wide. |
| Smoke density | 0 to 16 | 1 | Multiplier on the extinction of the smoke. The range was 0 to 4. |
| Smoke darkness | 0 to 1 | 0.3 | The share of the light that the smoke absorbs. At 0 the smoke is white. At 1 the smoke scatters 6 percent of the light. |
| Smoke billows | 0 to 1 | 0.5 | The strength of a noise field that breaks the smoke into puffs. The noise field rises at 9 m/s of real time. |

**Reason.** The Fuel texture stays as the Scenario gives it, because the Spread Solver reads the Fuel texture. The smoothing is in the terrain shader, in the fuel color layer only. The Fuel model Overlay, the burn scar, and the fire glow still read the cell, so the Fuel model Overlay shows the exact data. The smoke is a passive tracer, so a multiplier in the Smoke Pass gives the same picture as a larger `smokeRate`. Each Wind Particle is now one quad of two triangles in place of one line, because a line has no width.

**Open point.** `data/south-canyon/imagery/` holds an aerial image with one pixel per Fire Grid cell. A Scenario could carry such an image as a ground color layer. The Scenario format has no entry for one yet.

## D29: The Ground Image

**Question.** The fuel color layer shows one color per fuel code. For a real site, an aerial image shows the ground as it is. `data/south-canyon/imagery/` holds an aerial image with one pixel per Fire Grid cell. `01-data-layers.md` has no entry for a color image of the ground. How does a Scenario carry one?

**Answer.**

1. A new term: the Ground Image is an optional color image of the ground of a Scenario.
2. `scenario.json` has one more optional field, `groundImageFile`. The value is the name of a JPEG or PNG file in the Scenario folder. A Scenario with no such field, or with an empty value, has no Ground Image.
3. The Ground Image must cover exactly the Fire Grid, and the top row of the image must be the north edge, as in `fuel.png`. The image can have any size. The image should have `gridSize` pixels per side.
4. When `groundImageFile` names a file that is not in the folder, the Scenario loader stops the load with the `ScenarioFiles` check. When the file is not a JPEG or PNG image, the app logs a warning and loads the Scenario with no Ground Image.
5. The terrain shader draws the Ground Image in place of the fuel color. The Overlay, the burn scar, and the fire glow lie on top, as before. The Control Panel has a Ground Image opacity slider from 0 to 1, with a default of 1. At 0, the terrain shows the fuel color.
6. The app owns the Ground Image texture. The texture registry does not hold the Ground Image, because no simulation step reads it, and the Texture memory readout does not count it. A 4096 x 4096 image uses 64 MB with the mip levels.
7. `tools/import_canyon.py` has a `--ground-image` option. The script copies the file to the Scenario folder as `ground.jpg` or `ground.png` and writes the field. The script does not resample or georeference the image.

**Reason.** The Ground Image changes the picture only. The Spread Solver reads the Fuel texture and no color, so the fire is the same with and without the Ground Image.

**Results.** `south-canyon` now has the 2 m NAIP aerial image as the Ground Image. The Scenario loads in 0.41 s. The drainages of the image lie in the gullies of the Terrain Mesh, and the river lies on the valley floor, so the image has the right place and the right north. `elevation.r32` and `fuel.png` are identical to the files before.

**Open points.**

- The aerial image shows the vegetation of the image date, not of July 1994. The image also holds the sun shadows of the flight, which do not move with the Sim Clock.
- The image has not been checked against a surveyed point. The check is by eye.

## D30: Smoke light at night

**Question.** At night, with a high smoke density, the smoke glows blue-white over a black terrain. `04-visualization.md` gives the Smoke Pass the light of the sun and an orange glow near the fire. It gives no rule for the ambient light on the smoke. What light does the smoke get at night?

**Answer.**

1. The cause is the ambient term of the Smoke Pass. The term was 2 to 4 nits at night, and the terrain gets no light at night. The auto exposure of the camera stops at EV 2, which shows 2 nits as a bright gray.
2. The ambient luminance on the smoke at night is now 0.25 to 0.42 nits per color channel. By day the value is the same as before. The blend between the two uses the square of the day factor.
3. The smoke above a point shades the ambient light. Two samples straight up give the depth. The shade stops at 0.2, which stands for the light that scatters more than once.
4. The fire lights the smoke. At each step, the Smoke Pass reads Heat Flux Coarse below the point and at 12 points on two rings, with radii of 0.9 and 2.2 times the height above the ground. The sum sets an orange light of at most about 45 nits per full ring, and the light falls to half at 250 m above the ground. Smoke darkness lowers the light to 35 percent at most.
5. The orange glow of step 6 of the Smoke Pass stays as before.

**Reason.** A night smoke column has two light sources: the night sky, which is weak, and the fire below, which is strong and orange. The 45 nits of the fire light are too weak to see at a day exposure, so the day picture changes only through the shade of answer 3.

**Results.** At 21:20 solar time on `south-canyon`, with a smoke density of 16, the smoke is dark with an orange side near the fire line and a faint blue-gray far from the fire. The frame rate with the 13 more texture reads per step is 400 frames per second and more at 1280 x 720 in the Editor.

**Open points.**

- The values are set by eye from captures. No measurement of a real smoke column stands behind them.
- The terrain gets no light at night, so a night with a moon is not shown.

## D31: Sharper advection, vorticity confinement, and turbulence in the display

**Question.** A meteorologist saw the demo and expected more low-level turbulence where the wind meets the terrain. The Wind Particles and the smoke show a smooth flow. `03-weather-wind.md` gives the Wind Solver a semi-Lagrangian advect kernel and no turbulence. How does the project show more turbulence?

**Answer.** There are two causes, and each one has its own answer.

The first cause is the advect kernel. Each step blurs the field, so an eddy of a few cells fades in a few steps. Answers 1 to 4 change the Wind Solver.

1. A correct kernel follows the advect kernel (the MacCormack method). The correct kernel advects the result forward again and compares the result with the field before the step. Half of the difference, times `advectionSharpness`, goes back into the result. The result stays inside the range of the 8 texels around the traced point, so the kernel makes no new peak.
2. A cell next to the terrain keeps the `theta` of the advect kernel. The blur is the only mixing that takes the ground heat out of a cell with little flow. Without this rule, `theta` reaches 18 K in one hour of sun, and the fountains of decision D27 can come back.
3. The correction acts only where the grid can hold an eddy and the wind can make one. The share is multiplied by two weights. The first weight is 0 up to 80 m above the ground and 1 from 208 m. The second weight is 0 up to a wind speed of 3 m/s and 1 from 8 m/s. The layer near the ground holds the slope winds and the Surface Wind sample. On 32 m cells that layer needs the blur of the advect kernel as its mixing. Without the weights, three acceptance criteria of M3 fail (see Results).
4. A vorticity confinement force follows the buoyancy. The force has the same two weights. The default of `vorticityConfinement` is 0, so the force is off until the user moves the slider. The force is `vorticityConfinement * cellSize * (N x omega)`. `omega` is the vorticity, and `N` is the unit vector from low vorticity to high vorticity. The force turns with each eddy and gives back a part of the spin that the advect kernel removes. The change of one step stays under 20 percent of the wind speed at the face plus 0.02 m/s.

The second cause is the cell size. An eddy smaller than about 100 m cannot exist on 32 m cells. Answers 5 to 8 show such eddies in the display only.

5. A new texture, Turbulence, holds the vorticity and the turbulence speed of each air cell. The Wind Solver writes it at the end of each Wind step. The turbulence speed is the root of the sum of three squares:
   - Shear: `0.31 * cellSize * |S|`, a Smagorinsky estimate. `|S|` is the strain rate of the wind.
   - Ground: `2.2 * uStar`, with `uStar = 0.4 * speed / ln((z + z0) / z0)`, the log law. `z` is the height above the ground. The part falls to 0 at 600 m.
   - Heat: `0.6 * sqrt(g / theta0 * theta * cellSize)`, the free-fall speed of warm air over one cell.

   The turbulence speed stops at 15 m/s.
6. The Wind Particles add `turbulenceGain * turbulenceSpeed * field` to the velocity of each particle. The field is a sum of 6 plane waves with wavelengths from 44 m to 197 m, in `FireCATurbulence.hlsl`. The field has no divergence and a mean square of 1.
7. The Smoke Pass moves each sample point by `turbulenceGain * turbulenceSpeed * 14 s`, and by 70 m at most, along a smooth noise vector with scales of 180 m and 80 m. The Smoke Pass does not use the plane waves, because the steps of the ray march are longer than the short waves and the result shows stripes.
8. The turbulence of the display does not change the Wind Solver, the Surface Wind, or the fire. Whether gusts under the cell size must push the fire is an open point.
9. The Control Panel has a Wind Solver section with two sliders, "Advection sharpness" and "Vorticity confinement". The Display section has a slider "Turbulence in smoke and particles" from 0 to 3, with a default of 1. With both Wind Solver sliders at 0, the Wind Solver is the same as before. A point with no smoke before the move of answer 7 is skipped, which keeps the cost of the Smoke Pass low.
10. The texture registry has three more Wind Grid textures: Velocity Scratch, Smoke Scratch, and Turbulence. The texture memory at the 4096 size rises from 1202 MB to 1410 MB. The correct kernel writes the scratch textures, and the registry then exchanges each scratch texture with the read copy.

**Reason.** The answers keep the two things apart. Answers 1 to 4 are a change of the model, and the fire sees them through the Surface Wind. Answers 5 to 8 are a picture of something that the model cannot hold, with a size that comes from the model. The shapes of the small eddies are made up. Their strength and their place are not.

**Results.** The first design had no weights (answer 3) and a default confinement of 0.02. Three acceptance criteria of M3 then failed on `synthetic-canyon-01`. The table shows the steps to the final design.

| Design | Upslope at 14:00 (must be 70 percent or more) | Downslope at 02:00 (must be 70 percent or more) | Canyon floor off the axis (must be under 25 degrees) | Narrow section faster than upstream |
|---|---|---|---|---|
| No weights, confinement 0.02 | 37.0 percent | 64.2 percent | 22.3 degrees | No |
| No weights, confinement 0 | 21.7 percent | 62.9 percent | 11.5 degrees | No |
| Height weight, confinement 0.02 | 67.5 percent | 38.8 percent | 10.8 degrees | No |
| Both weights, confinement 0.02 | 92.9 percent | 94.6 percent | 30.8 degrees | Not measured |
| Both weights, confinement 0 (final) | 92.9 percent | 94.6 percent | 17.1 degrees | Yes (0.94 m/s against 0.28 m/s) |

With less blur, the upslope layer is thinner than one cell, and the Surface Wind sample at 48 m reads the return flow above it. The confinement makes the flow in the canyon unsteady, and the criteria read one moment of the flow.

The next table is `south-canyon`, sun and no fire, 1800 Wind steps after the spin-up, with the final design. The vorticity values are means over the air cells inside the relaxation zones.

| Setting | Mean vorticity (1/s) | Cells over 0.05 1/s | Peak wind (m/s) | Highest theta (K) | Wind step (ms) |
|---|---|---|---|---|---|
| Sharpness 0, confinement 0 (before) | 0.0084 | 5.0 percent | 22.6 | 8.8 | 5.1 |
| Sharpness 1, confinement 0 (default) | 0.0104 | 7.0 percent | 23.1 | 11.5 | 5.7 |
| Sharpness 1, confinement 0.02 | 0.0136 | 8.9 percent | 27.4 | 6.6 | 5.8 |
| Sharpness 1, confinement 0.06 | 0.0569 | 34.8 percent | 33.8 | 3.7 | 5.8 |

No run holds a NaN value. The default gives 23 percent more mean vorticity and 40 percent more cells with a strong vorticity. That is a modest change. The confinement gives more, at the price of the channeling criterion. Without the weights, a confinement of 0.1 fills the Wind Grid with noise, so the range stops at 0.06. The mean turbulence speed is 0.15 to 0.7 m/s over all air cells, and the highest values are 5 to 8 m/s. Near the ground the ground part is about a quarter of the wind speed. The Smoke Pass with the turbulence costs about 17 percent of the frame rate in the Editor at 1280 x 720 (369 frames per second without, 305 with).

**Open points.**

- The values of answers 3 to 6 are not checked against a measurement. A meteorologist must review the three parts of the turbulence speed.
- The low-level turbulence that the meteorologist asked for is in the display only. The Wind Solver cannot hold it on 32 m cells, and the layer under 80 m keeps the old advection. Finer cells near the ground are the real answer.
- The acceptance criteria of M3 read one moment of the flow. A solver with more eddies needs criteria that read a mean over time.
- The fire does not feel the small gusts (answer 7).
- The plane waves of the Wind Particles repeat in space. A view from far away can show the pattern at a high `turbulenceGain`.

## D32: The Wind Nest

**Question.** Decision D31 left the low-level turbulence out of the Wind Solver, because the 32 m cells cannot hold it. The user chose a nested grid over two other designs: thinner layers everywhere, and a grid that follows the terrain. How does the nested grid work?

**Answer.**

1. A new term: the Wind Nest is a second grid of the Wind Solver with smaller cells, over a box inside the Wind Grid. `Wind.compute` and the class `WindSolver` run both grids. A `WindDomain` gives the origin, the cell size, and the cell counts of a grid.
2. The Wind Nest has at most 192 x 128 x 192 cells. The cell size is 8 m, 12 m, or 16 m, and the default is 12 m. At 12 m the box is 2304 m wide and 1536 m high. On a Scenario that is narrower than the box, the Wind Nest has fewer cells.
3. The origin of the box is a multiple of the cell size, so each column covers whole Fire Grid cells. The bottom face is the lowest elevation in the box, rounded down to a multiple of the cell size.
4. The Wind Nest has its own Ground Height and Solid Mask, from the same kernels as the Wind Grid. At 12 m, a column is the mean of 6 x 6 Fire Grid cells, so the terrain of the Wind Nest has ridges and gullies that the Wind Grid does not have.
5. The coupling is one way. The Wind Nest starts from the state of the Wind Grid. In the relaxation zones, 12 cells wide at the lateral faces and 16 cells deep at the top face, the Wind Nest blends its velocity, `theta`, and smoke toward the Wind Grid, with a relaxation time of 6 s at the outer face. The Wind Nest does not change the Velocity of the Wind Grid.
6. The Wind Nest runs 2 steps after each Wind step, each with half of `dtWind`. The spin-up of the Wind Grid runs with no Wind Nest.
7. The ground sources of the Wind Nest read Surface Heating and Heat Flux Coarse of the Wind Grid at the place of each column. The smoke source is multiplied by 32 m over the cell size, so a column of the Wind Nest gets the same amount of smoke as a column of the Wind Grid.
8. The fire sees the Wind Nest. The Surface Wind kernel of the Wind Grid takes, inside the box, the mean of 9 points of the Surface Wind of the Wind Nest. The two values blend between one and two zone widths from the faces. The value is one Wind step old.
9. The weights of decision D31 are in cells, not in meters: the correct kernel and the vorticity confinement are off up to 2.5 cells above the ground and full from 6.5 cells. For the Wind Grid that is 80 m and 208 m, as before. For the Wind Nest at 12 m it is 30 m and 78 m. The ground part of the turbulence speed is multiplied by the cell size over 32 m.
10. The Smoke Pass and the Wind Particles read the Wind Nest inside the box, with a blend over the zone width at the lateral faces and the top face. A ray of the Smoke Pass that crosses the box has 40 steps before the box, 96 steps in the box, and 40 steps after it. The billows are 60 percent weaker in the box. The shader of the Smoke Pass has two variants, and the keyword `FIRECA_WIND_NEST` selects the variant with the Wind Nest when a Wind Nest is placed.
11. The app places the Wind Nest around the first ignition. The Control Panel has a toggle, a cell size slider, and a button that moves the Wind Nest to the point that the camera looks at. A move starts the Wind Nest again from the state of the Wind Grid.
12. The tests of M1 to M6 and the benchmark of decision D25 run with no Wind Nest. The Wind Nest has the criteria of M7.

**Reason.** A nested grid puts the cells where the fire is and leaves the rest of the model alone. The other designs cost more for less: thinner layers keep the 32 m width of a cell, and a grid that follows the terrain needs a new solver and does poorly on canyon walls. One-way coupling keeps the Wind Grid, and with it every result of M3 to M6, the same.

**Results.** All 42 PlayMode tests pass: the 39 tests of M1 to M6 with no Wind Nest, and 3 tests for the criteria of M7.

| Criterion of M7 | Result |
|---|---|
| 1. Flat flow, 128 x 128 x 128 cells of 8 m, 300 steps | Largest speed error 1.5 percent, largest direction error 0.12 degrees |
| 2. Divergence after the project kernel | 0.00015 1/s at most |
| 3. Vorticity under 150 m above the ground, `south-canyon` | Wind Nest 0.053 1/s, Wind Grid 0.030 1/s, ratio 1.76. Highest value 0.40 1/s against 0.12 1/s |
| 4. Fire run of 2 simulated hours | No NaN value, peak wind 28.7 m/s, highest `theta` 16.7 K |
| 5. Mean Surface Wind inside the box | Wind Grid (3.36, -1.22) m/s, Wind Nest (3.35, -1.20) m/s |
| 6. Texture memory of the Wind Nest at 12 m | 315 MB. The Stats HUD shows 1725 MB with the Wind Nest |

Criterion 2 first copied criterion 1 of M3: a value after the project kernel under 10 percent of the value before the pressure kernel. The Wind Nest gave 18 to 33 percent, with 30 to 60 iterations and over-relaxation factors from 1.6 to 1.85. The step of the Wind Nest is half as long and starts from a field with little divergence (0.0004 1/s), so the ratio is high while the value is low. The criterion is now the value.

Release 0.3.0 showed two faults in the standalone benchmark.

- The Smoke Pass took 5.0 ms with no Wind Nest, up from 3.8 ms in release 0.2.0. The march had one code path, with the reads of the Wind Nest behind a test at run time. The shader now has a variant with no code of the Wind Nest. With that variant the Smoke Pass takes 4.3 ms, and the player of release 0.2.0 takes 4.3 ms on the same day.
- The paused frame rates were 289 fps to 336 fps, down from 328 fps to 368 fps. The benchmark turned the Wind Nest off after the paused measurements, and the app had placed the Wind Nest at the first ignition. The benchmark now turns the Wind Nest off at the start, and the paused frame rates are 329 fps to 368 fps. With a Wind Nest placed and no smoke, the Smoke Pass takes about 0.9 ms, against about 0.4 ms with no Wind Nest.

The benchmark report has a new field, `pausedSmokeOffFps`: the paused frame rate from the Chute preset with the smoke off.

Cost on the RTX 5090: one Wind step with the 2 steps of the Wind Nest takes 12.4 ms to 16.8 ms, against 5.7 ms for the Wind Grid alone. That is under the limit of 3 times the cost that M7 sets. In the Editor at 1280 x 720 and a time scale of 60, the frame rate is about 235 frames per second with the Wind Nest and about 300 without. At a requested time scale of 1000, the achieved time scale is about 120 to 155 with the Wind Nest and about 340 without.

The fire changes with the Wind Nest. In a run of 4 simulated hours on `south-canyon` from the Scenario ignition, the burned area is 14.3 ha with the Wind Nest and 7.0 ha without. The mean Surface Wind speed in the box is 3.3 m/s with the Wind Nest and 2.8 m/s without. Captures show no seam at the faces of the box, in the smoke or in the Wind Particles.

**Open points.**

- The coupling is one way. The Wind Grid runs its own, coarser plume in the same box, and the fire heat does not come back from the Wind Nest to the Wind Grid.
- The Wind Nest reads Heat Flux Coarse, which is a mean over 32 m. A fire line is a few meters wide. A heat flux at the cell size of the Wind Nest is a later step.
- The fire reads the Surface Wind at 32 m, so it sees the mean of the Wind Nest and not each gust.
- The Wind Nest does not follow the fire. The user moves it, and a move loses the eddies of the Wind Nest for about a minute of simulated time.
- The slope wind criteria of M3 are not run on the Wind Nest.
- The slice plane and the Overlays show the Wind Grid only.
