import json, numpy as np, os, sys
from PIL import Image
from geo import X0,Y0,X1,Y1
Image.MAX_IMAGE_PIXELS=None
D=sys.argv[1]
el=np.load("elev_north_up.npy").astype("<f4")
el[::-1].tofile(f"{D}/scenario/elevation.r32")            # south row first
Image.fromarray(el).save(f"{D}/source/elevation_2m_utm13n.tif")
fu=np.array(Image.open("lf_FBFM13.tif")).astype(np.uint8)
Image.fromarray(fu,"L").save(f"{D}/scenario/fuel.png")     # top row north
import shutil; shutil.copyfile("lf_FBFM13.tif",f"{D}/source/landfire_lf2025_fbfm13_2m_nearest.tif")
shutil.copyfile("lf_FBFM40.tif",f"{D}/source/landfire_lf2025_fbfm40_2m_nearest.tif")
def world(path,res):
    open(path,"w").write(f"{res}\n0\n0\n-{res}\n{X0+res/2}\n{Y1-res/2}\n")
world(f"{D}/source/elevation_2m_utm13n.tfw",2)
n=Image.open("naip_mosaic.png")
n.save(f"{D}/imagery/naip_1m_8192.jpg",quality=92); world(f"{D}/imagery/naip_1m_8192.jgw",1)
n.resize((4096,4096),Image.LANCZOS).save(f"{D}/imagery/naip_2m_4096.jpg",quality=92); world(f"{D}/imagery/naip_2m_4096.jgw",2)
Image.open("topo_mosaic.png").save(f"{D}/imagery/usgs_topo_2m_4096.png"); world(f"{D}/imagery/usgs_topo_2m_4096.pgw",2)
Image.open("hill_mosaic.png").save(f"{D}/imagery/hillshade_4m_2048.png"); world(f"{D}/imagery/hillshade_4m_2048.pgw",4)
sc={"name":"south-canyon","gridSize":4096,"cellSizeM":2.0,"elevationFile":"elevation.r32","fuelFile":"fuel.png",
 "latitudeDeg":39.578,"dayOfYear":187,"startSolarHour":12.0,
 "weather":{"tempMaxC":32.0,"tempMinC":15.0,"rhMinPct":8.0,"referenceElevationM":1731.0,"lapseRateCPerKm":6.5,"stabilityKPerM":0.003,
  "wind":[{"hour":0.0,"speedMps":5.0,"fromDeg":200.0},{"hour":1.3,"speedMps":8.0,"fromDeg":220.0},{"hour":2.2,"speedMps":11.0,"fromDeg":260.0},{"hour":2.8,"speedMps":13.0,"fromDeg":270.0},{"hour":6.8,"speedMps":6.0,"fromDeg":315.0}]},
 "moisture":{"m1Pct":3.5,"m10Pct":5.0,"m100Pct":7.0,"livePct":100.0},
 "ignitions":[{"hour":0.0,"xM":3288.0,"zM":3680.0,"radiusM":6.0}]}
json.dump(sc,open(f"{D}/scenario/scenario.json","w"),indent=2)
# perimeters
d=json.load(open("perims.json")); g=json.load(open("perims_grid_m.json"))
feats=[{"type":"Feature","properties":f["attributes"],"geometry":{"type":"Polygon","coordinates":f["geometry"]["rings"]}} for f in d["features"]]
json.dump({"type":"FeatureCollection","features":feats},open(f"{D}/reference/fire_perimeters_wgs84.geojson","w"))
json.dump({"note":"Rings in grid meters: [xM east of the southwest corner, zM north of the southwest corner]","perimeters":g},open(f"{D}/reference/fire_perimeters_grid_m.json","w"))
# checks
e2=np.fromfile(f"{D}/scenario/elevation.r32","<f4").reshape(4096,4096)
print("r32 bytes",os.path.getsize(f"{D}/scenario/elevation.r32"),"expected",4096*4096*4)
print("elev min max",e2.min(),e2.max(),"range",e2.max()-e2.min(),"finite",np.isfinite(e2).all())
print("ignition cell elev",e2[3680//2,3288//2],"fuel there",fu[4095-3680//2,3288//2])
f2=np.array(Image.open(f"{D}/scenario/fuel.png")); print("fuel png",f2.shape,f2.dtype,dict(zip(*[x.tolist() for x in np.unique(f2,return_counts=True)])))
print("SW corner elev",e2[0,0],"NE",e2[-1,-1]); print("bbox",X0,Y0,X1,Y1)
