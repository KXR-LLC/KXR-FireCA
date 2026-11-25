# South Canyon Data

## TL;DR

- This folder holds real-world data for the South Canyon Fire site on Storm King Mountain, Colorado, cut to one 8192 m x 8192 m box.
- `scenario/` is a complete Scenario in the format of `docs/01-data-layers.md`. Copy `scenario/` to `unity/Assets/StreamingAssets/Scenarios/south-canyon/` to load the canyon.
- `imagery/` holds an aerial image, a topo map image, and a hillshade. All three images cover exactly the Fire Grid.
- `reference/` holds the Forest Service fire behavior report, the USGS topo quad, and fire perimeters.
- The elevation and fuel values are real. Several weather values in `scenario.json` are assumptions. The Assumptions table lists each one.
- The data is 250 MB. Do not commit the folder to git without a decision on large files.

## Georeference

Every raster in this folder covers the same box. The coordinate system is NAD83 / UTM zone 13N (EPSG:26913).

| Property | Value |
|---|---|
| West edge (X = 0) | 288914 m E |
| South edge (Z = 0) | 4379600 m N |
| East edge | 297106 m E |
| North edge | 4387792 m N |
| Box center | 39.5780 N, 107.4100 W |
| Fire Grid | 4096 x 4096 cells, 2 m per cell |

To convert a UTM coordinate to a world coordinate, subtract 288914 from the easting to get X and subtract 4379600 from the northing to get Z.

Each image in `imagery/` has a world file (`.jgw` or `.pgw`) with the same georeference.

## Contents

| Path | Content |
|---|---|
| `scenario/elevation.r32` | Ground elevation, 4096 x 4096 float32, south row first. Range 1710 m to 2956 m. |
| `scenario/fuel.png` | LANDFIRE FBFM13 fuel codes, 8-bit grayscale, north row first |
| `scenario/scenario.json` | Scenario settings for the afternoon of July 6, 1994 |
| `source/elevation_2m_utm13n.tif` | The elevation as a float32 TIFF, north row first, with a world file |
| `source/landfire_lf2025_fbfm13_2m_nearest.tif` | The FBFM13 export as delivered by the LANDFIRE service |
| `source/landfire_lf2025_fbfm40_2m_nearest.tif` | The Scott and Burgan 40 fuel models for the same box, for later use |
| `imagery/naip_1m_8192.jpg` | NAIP natural color aerial image, 1 m per pixel |
| `imagery/naip_2m_4096.jpg` | The same aerial image at 2 m per pixel, one pixel per Fire Grid cell |
| `imagery/usgs_topo_2m_4096.png` | USGS topo map image, 2 m per pixel |
| `imagery/hillshade_4m_2048.png` | Multidirectional hillshade from the 3DEP service, 4 m per pixel |
| `reference/RMRS-RP-9_south_canyon_fire_behavior.pdf` | Butler and others (1998), the fire behavior reconstruction |
| `reference/USTopo_Storm_King_Mountain_CO_2022.pdf` | US Topo 7.5-minute quad, GeoPDF |
| `reference/fire_perimeters_wgs84.geojson` | Fire perimeters that touch the box, in longitude and latitude |
| `reference/fire_perimeters_grid_m.json` | The same perimeters in world coordinates (X, Z) in meters |
| `preview/` | Small preview images, including the hillshade with the 1994 perimeter in red |
| `fetch/` | The Python scripts that downloaded and assembled the data |

## Sources

All sources are United States government products in the public domain.

| Layer | Source | Notes |
|---|---|---|
| Elevation | USGS 3DEP, `3DEPElevation` ImageServer at elevation.nationalmap.gov | Bilinear export at 2 m. The source lidar is the 1 m project CO_Central_Western_2016. |
| Fuel | LANDFIRE LF 2025, `LF2025_FBFM13_CONUS` ImageServer at lfps.usgs.gov | Nearest-neighbor export at 2 m. The native cell is 30 m. |
| Aerial | USGS NAIP imagery ImageServer at imagery.nationalmap.gov | Bands 1 to 3, natural color |
| Topo image | USGS Topo base map at basemap.nationalmap.gov | |
| Topo quad | US Topo, Storm King Mountain, CO, 2022, from The National Map | The east edge of the box extends about 1 km into the Glenwood Springs quad. |
| Perimeters | NIFC InterAgency Fire Perimeter History | "So. Canyon" 1994 (BLM), 2198 acres. The file also holds Coal Seam 2002 and two smaller fires. |
| Report | USDA Forest Service Research Paper RMRS-RP-9 | |

The raw 1 m elevation tiles are not in this folder. Each tile covers 10 km x 10 km. The four tiles that cover the box are `x28y438`, `x28y439`, `x29y438`, and `x29y439` under:

```text
https://prd-tnm.s3.amazonaws.com/StagedProducts/Elevation/1m/Projects/CO_Central_Western_2016/TIFF/
```

## Fuel codes in the box

| Code | Meaning | Share of cells |
|---|---|---|
| 2 | Timber grass and understory | 8.2 percent |
| 5 | Brush | 37.7 percent |
| 8 | Closed timber litter | 31.6 percent |
| 9 | Hardwood litter | 6.4 percent |
| 10 | Timber litter and understory | 9.4 percent |
| 12 | Medium slash | under 0.1 percent |
| 91 | Urban | 2.9 percent |
| 93 | Agriculture | 0.3 percent |
| 98 | Water | 0.5 percent |
| 99 | Barren | 2.9 percent |

## Assumptions in scenario.json

| Field | Value | Basis |
|---|---|---|
| `dayOfYear` | 187 | July 6, 1994 |
| `startSolarHour` | 12.0 | About 13:10 Mountain Daylight Time. The blowup started near 16:00 MDT. |
| `rhMinPct` | 8 | RMRS-RP-9: minimum of 8 percent at the Rifle weather station at 17:00 |
| `m1Pct` | 3.5 | RMRS-RP-9: fine dead fuel moisture estimated at 2 to 5 percent |
| `livePct` | 100 | RMRS-RP-9: about 125 percent in green Gambel oak and about 60 percent in underburned oak, measured July 12 |
| `wind` directions and timing | South-southwest, then west after hour 2.2 | RMRS-RP-9: the cold front passed the fire near 15:20 MDT and the winds peaked shortly after 16:00 |
| `wind` speeds | 5 to 13 m/s | **Assumption.** RMRS-RP-9 gives 30 mph at Grand Junction and gusts to 45 mph at Rifle. The file uses lower sustained values for the open-ground wind at 6.1 m. |
| `tempMaxC`, `tempMinC` | 32, 15 | **Assumption.** RMRS-RP-9 gives no temperature for July 6. |
| `m10Pct`, `m100Pct` | 5, 7 | **Assumption.** |
| `referenceElevationM` | 1731 | Elevation of the Colorado River in the report (5,680 ft) |
| `ignitions[0]` | X 3288, Z 3680 | **Estimate.** RMRS-RP-9 places the lightning ignition on a knob at 6,980 ft at the southwest end of the Main Ridge. The point is a knob at 6,983 ft inside the west part of the 1994 perimeter. Check the point against Figure 8 and the maps in the report. |

## Cautions

- **Fuel date.** LANDFIRE LF 2025 describes the vegetation of 2025. The site burned in 1994 and most of the box burned again in the 2002 Coal Seam Fire. The 1994 fuels were Gambel oak, pinyon-juniper, and cured grass, as the report describes. To reproduce 1994, repaint `fuel.png` from the fuel descriptions in the report.
- **Fuel resolution.** The fuel layer has 30 m source cells. The layer looks blocky on the 2 m Fire Grid.
- **Elevation resampling.** The elevation came from the USGS dynamic service at 2 m, not from the raw 1 m tiles. For a pixel-exact cut, download the four tiles and resample.
- **Fatality site.** Fourteen firefighters died on this fire on July 6, 1994. Treat the site and the names in the report with respect in any demo.
- **Imagery date.** The NAIP service returns the newest mosaic. The aerial image shows the present vegetation, not the 1994 vegetation.

## Checks done

1. The size of `elevation.r32` is 67,108,864 bytes, equal to 4096 x 4096 x 4.
2. Every elevation value is finite. The elevation range is 1246 m, under the 3000 m limit of the Scenario loader.
3. `fuel.png` holds only the codes in the fuel table above.
4. The 1994 perimeter, projected to UTM, has an area of 2201 acres. The NIFC record lists 2198 acres. The match confirms the projection.
5. The hillshade, the aerial image, and the topo image align with the perimeter and with each other by eye.
