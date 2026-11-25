"""Real canyon import for KXR-FireCA (milestone M6).

The script converts a USGS 3DEP elevation GeoTIFF and a LANDFIRE FBFM13 GeoTIFF into a Scenario folder:
elevation.r32, fuel.png, and scenario.json, in the format of docs/01-data-layers.md.

The script resamples both rasters to the Fire Grid: bilinear for elevation, nearest neighbor for fuel.

Readers:
    1. GDAL (the osgeo package). GDAL reprojects a raster from any coordinate system to the target system.
    2. tifffile and NumPy, when GDAL is not installed. This reader needs both rasters in the same projected
       coordinate system, in meters, with north up. It reads the georeference from the GeoTIFF tags or
       from a world file (.tfw) next to the raster.

Usage:
    python import_canyon.py --elevation dem.tif --fuel fbfm13.tif --out <Scenario folder>
        [--name south-canyon] [--grid-size 4096] [--west E --south N] [--settings scenario.json]
        [--ground-image image.jpg] [--latitude DEG] [--utm-zone N] [--reader auto|gdal|tifffile]

    --west, --south   The southwest corner of the Fire Grid, in the coordinate system of the elevation raster.
                      The default is the southwest corner of the elevation raster.
    --settings        A scenario.json file with weather, moisture, date, and ignition values to keep.
                      The script always writes the name, the grid values, the file names, and the latitude.
    --ground-image    A JPEG or PNG color image that covers exactly the Fire Grid, with the north row first.
                      The script copies the file to the Scenario folder and does not resample it.
"""
import argparse
import json
import shutil
import math
import os
import sys

import numpy as np

CELL_SIZE_M = 2.0
VALID_GRID_SIZES = (512, 1024, 2048, 4096)
MIN_ELEVATION_M = -500.0
MAX_ELEVATION_M = 9000.0
MAX_ELEVATION_RANGE_M = 3000.0

# The default values of docs/01-data-layers.md. A real canyon has no default ignition.
DEFAULT_SETTINGS = {
    "dayOfYear": 213,
    "startSolarHour": 13.0,
    "weather": {
        "tempMaxC": 35.0, "tempMinC": 16.0, "rhMinPct": 12.0, "referenceElevationM": 1200.0,
        "lapseRateCPerKm": 6.5, "stabilityKPerM": 0.003,
        "wind": [{"hour": 0.0, "speedMps": 4.0, "fromDeg": 225.0}, {"hour": 3.0, "speedMps": 7.0, "fromDeg": 250.0}],
    },
    "moisture": {"m1Pct": 5.0, "m10Pct": 6.0, "m100Pct": 8.0, "livePct": 80.0},
    "ignitions": [],
}


class ImportError_(Exception):
    """A problem that stops the import, with a message for the user."""


# ---- georeference ----

class Raster:
    """A north-up raster: values[row, column], row 0 at the north edge, with the west and north edges in meters."""

    def __init__(self, values, west, north, pixel_size, epsg=None, nodata=None):
        self.values = values
        self.west = west
        self.north = north
        self.pixel_size = pixel_size
        self.epsg = epsg
        self.nodata = nodata

    @property
    def south(self):
        return self.north - self.values.shape[0] * self.pixel_size

    @property
    def east(self):
        return self.west + self.values.shape[1] * self.pixel_size


def read_with_tifffile(path):
    import tifffile
    with tifffile.TiffFile(path) as tif:
        page = tif.pages[0]
        values = page.asarray()
        if values.ndim != 2:
            raise ImportError_(f"{path}: the raster must have one band.")
        geo = tif.geotiff_metadata or {}
        nodata = None
        if "GDAL_NODATA" in page.tags:
            try:
                nodata = float(str(page.tags["GDAL_NODATA"].value).strip().strip("\x00"))
            except ValueError:
                nodata = None
    epsg = None
    key = geo.get("ProjectedCSTypeGeoKey")
    if key is not None:
        epsg = int(getattr(key, "value", key))

    if "ModelPixelScale" in geo and "ModelTiepoint" in geo:
        sx, sy = geo["ModelPixelScale"][0], geo["ModelPixelScale"][1]
        tie = geo["ModelTiepoint"]
        west = tie[3] - tie[0] * sx
        north = tie[4] + tie[1] * sy
    else:
        world = os.path.splitext(path)[0] + ".tfw"
        if not os.path.exists(world):
            raise ImportError_(f"{path}: the raster has no GeoTIFF georeference and no world file (.tfw).")
        a, d, b, e, c, f = [float(line) for line in open(world).read().split()]
        if b != 0.0 or d != 0.0:
            raise ImportError_(f"{world}: a rotated raster needs the GDAL reader.")
        sx, sy = a, -e
        # A world file gives the center of the top left pixel.
        west = c - 0.5 * sx
        north = f + 0.5 * sy
    if abs(sx - sy) > 1e-9:
        raise ImportError_(f"{path}: the pixels are not square.")
    return Raster(values, west, north, sx, epsg, nodata)


def resample_numpy(raster, west, south, grid_size, bilinear):
    """Resamples a raster to the Fire Grid. Returns values[row, column] with row 0 at the north edge."""
    from scipy import ndimage
    north = south + grid_size * CELL_SIZE_M
    if (west < raster.west - 1e-6 or west + grid_size * CELL_SIZE_M > raster.east + 1e-6
            or south < raster.south - 1e-6 or north > raster.north + 1e-6):
        raise ImportError_("The Fire Grid box extends past the raster. "
                           f"Raster: west {raster.west}, south {raster.south}, east {raster.east}, north {raster.north}.")
    same_grid = (abs(raster.pixel_size - CELL_SIZE_M) < 1e-9
                 and abs((west - raster.west) / CELL_SIZE_M - round((west - raster.west) / CELL_SIZE_M)) < 1e-6
                 and abs((raster.north - north) / CELL_SIZE_M - round((raster.north - north) / CELL_SIZE_M)) < 1e-6)
    if same_grid:
        # The raster is on the Fire Grid already. The result is a plain cut, with no change of a value.
        col = int(round((west - raster.west) / CELL_SIZE_M))
        row = int(round((raster.north - north) / CELL_SIZE_M))
        return raster.values[row:row + grid_size, col:col + grid_size]
    # Centers of the Fire Grid cells, in pixel coordinates of the raster.
    index = np.arange(grid_size, dtype=np.float64) + 0.5
    cols = (west + index * CELL_SIZE_M - raster.west) / raster.pixel_size - 0.5
    rows = (raster.north - (north - index * CELL_SIZE_M)) / raster.pixel_size - 0.5
    grid_rows, grid_cols = np.meshgrid(rows, cols, indexing="ij")
    return ndimage.map_coordinates(raster.values, [grid_rows, grid_cols], order=1 if bilinear else 0, mode="nearest")


def read_and_resample_gdal(path, west, south, grid_size, bilinear, target_srs):
    """Warps a raster to the Fire Grid with GDAL. Returns (values, srs WKT, nodata)."""
    from osgeo import gdal
    gdal.UseExceptions()
    source = gdal.Open(path)
    if source is None:
        raise ImportError_(f"{path}: GDAL cannot open the raster.")
    srs = target_srs or source.GetProjection()
    if not srs:
        raise ImportError_(f"{path}: the raster has no coordinate system. Pass a raster with a projected coordinate system in meters.")
    if west is None or south is None:
        gt = source.GetGeoTransform()
        west = gt[0]
        south = gt[3] + gt[5] * source.RasterYSize
    north = south + grid_size * CELL_SIZE_M
    east = west + grid_size * CELL_SIZE_M
    warped = gdal.Warp("", source, format="MEM", dstSRS=srs, outputBounds=(west, south, east, north),
                       xRes=CELL_SIZE_M, yRes=CELL_SIZE_M,
                       resampleAlg=gdal.GRA_Bilinear if bilinear else gdal.GRA_NearestNeighbour)
    band = warped.GetRasterBand(1)
    return band.ReadAsArray(), srs, band.GetNoDataValue(), west, south


def utm_to_latitude(easting, northing, zone, northern=True):
    """Latitude of a UTM coordinate on the GRS80 ellipsoid, degrees. The error is under 1e-6 degrees."""
    a, f = 6378137.0, 1.0 / 298.257222101
    k0 = 0.9996
    e2 = f * (2.0 - f)
    ep2 = e2 / (1.0 - e2)
    x = easting - 500000.0
    y = northing if northern else northing - 10000000.0
    m = y / k0
    mu = m / (a * (1.0 - e2 / 4.0 - 3.0 * e2 ** 2 / 64.0 - 5.0 * e2 ** 3 / 256.0))
    e1 = (1.0 - math.sqrt(1.0 - e2)) / (1.0 + math.sqrt(1.0 - e2))
    phi1 = (mu + (3.0 * e1 / 2.0 - 27.0 * e1 ** 3 / 32.0) * math.sin(2.0 * mu)
            + (21.0 * e1 ** 2 / 16.0 - 55.0 * e1 ** 4 / 32.0) * math.sin(4.0 * mu)
            + (151.0 * e1 ** 3 / 96.0) * math.sin(6.0 * mu) + (1097.0 * e1 ** 4 / 512.0) * math.sin(8.0 * mu))
    n1 = a / math.sqrt(1.0 - e2 * math.sin(phi1) ** 2)
    t1 = math.tan(phi1) ** 2
    c1 = ep2 * math.cos(phi1) ** 2
    r1 = a * (1.0 - e2) / (1.0 - e2 * math.sin(phi1) ** 2) ** 1.5
    d = x / (n1 * k0)
    phi = phi1 - (n1 * math.tan(phi1) / r1) * (
        d ** 2 / 2.0 - (5.0 + 3.0 * t1 + 10.0 * c1 - 4.0 * c1 ** 2 - 9.0 * ep2) * d ** 4 / 24.0
        + (61.0 + 90.0 * t1 + 298.0 * c1 + 45.0 * t1 ** 2 - 252.0 * ep2 - 3.0 * c1 ** 2) * d ** 6 / 720.0)
    return math.degrees(phi)


def utm_zone_of_epsg(epsg):
    """The UTM zone and hemisphere of an EPSG code, or None."""
    if epsg is None:
        return None
    if 26901 <= epsg <= 26923:      # NAD83 / UTM north
        return epsg - 26900, True
    if 32601 <= epsg <= 32660:      # WGS 84 / UTM north
        return epsg - 32600, True
    if 32701 <= epsg <= 32760:      # WGS 84 / UTM south
        return epsg - 32700, False
    return None


def latitude_gdal(srs_wkt, x, y):
    from osgeo import osr
    source = osr.SpatialReference()
    source.ImportFromWkt(srs_wkt)
    target = osr.SpatialReference()
    target.ImportFromEPSG(4326)
    target.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    source.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    lon, lat, _ = osr.CoordinateTransformation(source, target).TransformPoint(x, y)
    return lat


# ---- Scenario ----

def build_settings(args, latitude, grid_size):
    data = json.loads(json.dumps(DEFAULT_SETTINGS))
    if args.settings:
        template = json.load(open(args.settings))
        for key in ("dayOfYear", "startSolarHour", "weather", "moisture", "ignitions", "cameraPresets", "landmarks"):
            if key in template:
                data[key] = template[key]
        stated = template.get("latitudeDeg")
        if stated is not None and abs(stated - latitude) > 0.05:
            print(f"warning: the settings file states latitude {stated}, and the Fire Grid center is at {latitude:.4f}. "
                  "The script writes the latitude of the Fire Grid center.")
    scenario = {
        "name": args.name or os.path.basename(os.path.normpath(args.out)),
        "gridSize": grid_size,
        "cellSizeM": CELL_SIZE_M,
        "elevationFile": "elevation.r32",
        "fuelFile": "fuel.png",
        "latitudeDeg": round(latitude, 4),
    }
    scenario.update(data)
    return scenario


def validate(scenario, elevation, fuel):
    """The validation checks of docs/01-data-layers.md. Returns a list of the names of the failed checks."""
    failed = []
    n = scenario["gridSize"]
    if n not in VALID_GRID_SIZES:
        failed.append("GridSize")
    if scenario["cellSizeM"] != CELL_SIZE_M:
        failed.append("CellSize")
    if elevation.size * 4 != n * n * 4 or elevation.dtype != np.dtype("<f4"):
        failed.append("ElevationFileSize")
    if not np.isfinite(elevation).all() or elevation.min() < MIN_ELEVATION_M or elevation.max() > MAX_ELEVATION_M:
        failed.append("ElevationValues")
    elif not float(elevation.max()) - float(elevation.min()) < MAX_ELEVATION_RANGE_M:
        failed.append("ElevationRange")
    if fuel.shape != (n, n) or fuel.dtype != np.uint8:
        failed.append("FuelImageSize")
    wind = scenario["weather"].get("wind", [])
    if len(wind) < 1 or any(wind[i]["hour"] <= wind[i - 1]["hour"] for i in range(1, len(wind))):
        failed.append("WindEntries")
    if not scenario["weather"]["tempMaxC"] > scenario["weather"]["tempMinC"]:
        failed.append("TemperatureRange")
    if any(not 1.0 <= scenario["moisture"][k] <= 300.0 for k in ("m1Pct", "m10Pct", "m100Pct", "livePct")):
        failed.append("MoistureValues")
    extent = n * CELL_SIZE_M
    for ig in scenario.get("ignitions", []):
        r = ig["radiusM"]
        if r < 0 or ig["xM"] - r < 0 or ig["xM"] + r > extent or ig["zM"] - r < 0 or ig["zM"] + r > extent:
            failed.append("IgnitionInsideGrid")
            break
    return failed


def main(argv=None):
    parser = argparse.ArgumentParser(description="Convert a 3DEP elevation raster and a LANDFIRE FBFM13 raster into a KXR-FireCA Scenario.")
    parser.add_argument("--elevation", required=True, help="USGS 3DEP elevation GeoTIFF, meters")
    parser.add_argument("--fuel", required=True, help="LANDFIRE FBFM13 GeoTIFF")
    parser.add_argument("--out", required=True, help="Scenario folder to write")
    parser.add_argument("--name", help="Scenario name. The default is the name of the Scenario folder.")
    parser.add_argument("--grid-size", type=int, default=4096, choices=VALID_GRID_SIZES)
    parser.add_argument("--west", type=float, help="West edge of the Fire Grid, in the coordinate system of the elevation raster")
    parser.add_argument("--south", type=float, help="South edge of the Fire Grid")
    parser.add_argument("--settings", help="scenario.json with weather, moisture, date, and ignition values to keep")
    parser.add_argument("--ground-image", help="JPEG or PNG color image that covers exactly the Fire Grid, north row first. "
                        "The script copies the file to the Scenario folder as the Ground Image.")
    parser.add_argument("--latitude", type=float, help="Latitude of the site, degrees. The default comes from the coordinate system.")
    parser.add_argument("--utm-zone", type=int, help="UTM zone of the rasters, for the tifffile reader when a raster has no EPSG code")
    parser.add_argument("--reader", choices=("auto", "gdal", "tifffile"), default="auto")
    args = parser.parse_args(argv)

    reader = args.reader
    if reader == "auto":
        try:
            import osgeo  # noqa: F401
            reader = "gdal"
        except ImportError:
            reader = "tifffile"
            print("note: GDAL is not installed. The tifffile reader needs both rasters in the same projected coordinate system.")
    n = args.grid_size

    try:
        if reader == "gdal":
            elevation, srs, elevation_nodata, west, south = read_and_resample_gdal(args.elevation, args.west, args.south, n, True, None)
            fuel_raw, _, _, _, _ = read_and_resample_gdal(args.fuel, west, south, n, False, srs)
            latitude = args.latitude if args.latitude is not None else latitude_gdal(
                srs, west + 0.5 * n * CELL_SIZE_M, south + 0.5 * n * CELL_SIZE_M)
        else:
            elevation_raster = read_with_tifffile(args.elevation)
            fuel_raster = read_with_tifffile(args.fuel)
            if elevation_raster.epsg and fuel_raster.epsg and elevation_raster.epsg != fuel_raster.epsg:
                raise ImportError_(f"The rasters have different coordinate systems (EPSG {elevation_raster.epsg} and "
                                   f"{fuel_raster.epsg}). Install GDAL, or reproject the fuel raster first.")
            west = args.west if args.west is not None else elevation_raster.west
            south = args.south if args.south is not None else elevation_raster.south
            elevation = resample_numpy(elevation_raster, west, south, n, True)
            fuel_raw = resample_numpy(fuel_raster, west, south, n, False)
            elevation_nodata = elevation_raster.nodata
            latitude = args.latitude
            if latitude is None:
                zone = (args.utm_zone, True) if args.utm_zone else (
                    utm_zone_of_epsg(elevation_raster.epsg) or utm_zone_of_epsg(fuel_raster.epsg))
                if zone is None:
                    raise ImportError_("The latitude is unknown. Pass --latitude or --utm-zone.")
                latitude = utm_to_latitude(west + 0.5 * n * CELL_SIZE_M, south + 0.5 * n * CELL_SIZE_M, zone[0], zone[1])

        elevation = np.ascontiguousarray(elevation, dtype="<f4")
        if elevation_nodata is not None and (elevation == np.float32(elevation_nodata)).any():
            raise ImportError_("The elevation raster has no-data cells inside the Fire Grid box.")
        # A fuel value outside 0 to 255 is no data. The Scenario loader treats code 0 as not burnable.
        fuel_raw = np.asarray(fuel_raw)
        fuel = np.where((fuel_raw >= 0) & (fuel_raw <= 255), fuel_raw, 0).astype(np.uint8)

        scenario = build_settings(args, latitude, n)
        failed = validate(scenario, elevation, fuel)
        if failed:
            raise ImportError_("The Scenario fails the validation checks: " + ", ".join(failed) + ".")
    except ImportError_ as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    from PIL import Image
    os.makedirs(args.out, exist_ok=True)
    # elevation.r32: the first row is the south edge. The rasters have the north row first.
    elevation[::-1].tofile(os.path.join(args.out, "elevation.r32"))
    # fuel.png: the top row of the image is the north edge.
    Image.fromarray(fuel, "L").save(os.path.join(args.out, "fuel.png"))
    if args.ground_image:
        # The Ground Image: the script copies the file and does not resample it. The image must be square.
        Image.MAX_IMAGE_PIXELS = None
        with Image.open(args.ground_image) as image:
            if image.width != image.height:
                print(f"error: the Ground Image is {image.width} x {image.height}. The image must be square.", file=sys.stderr)
                return 1
        ground_name = "ground" + os.path.splitext(args.ground_image)[1].lower()
        shutil.copyfile(args.ground_image, os.path.join(args.out, ground_name))
        scenario["groundImageFile"] = ground_name
    with open(os.path.join(args.out, "scenario.json"), "w") as handle:
        json.dump(scenario, handle, indent=2)
        handle.write("\n")

    codes, counts = np.unique(fuel, return_counts=True)
    burnable = int(counts[(codes >= 1) & (codes <= 13)].sum())
    print(f"wrote {args.out}")
    print(f"  reader: {reader}")
    print(f"  Fire Grid: {n} x {n} cells, west {west:.1f}, south {south:.1f}")
    print(f"  latitude: {scenario['latitudeDeg']}")
    print(f"  elevation: {float(elevation.min()):.1f} m to {float(elevation.max()):.1f} m")
    print(f"  fuel codes: {dict(zip(codes.tolist(), counts.tolist()))}")
    print(f"  burnable cells: {100.0 * burnable / fuel.size:.1f} percent")
    print("  validation: every check passes")
    return 0


if __name__ == "__main__":
    sys.exit(main())
