import json, numpy as np
from PIL import Image, ImageDraw
from geo import utm_fwd, X0, Y0
el=np.load("elev_north_up.npy")  # row0 north, 2 m
d=json.load(open("perims.json"))
polys={}
for f in d["features"]:
    a=f["attributes"]; key=f"{a['INCIDENT']}_{a['FIRE_YEAR_INT']}_{a['AGENCY']}"
    rings=[[utm_fwd(lat,lon) for lon,lat in r] for r in f["geometry"]["rings"]]
    polys[key]=rings
json.dump({k:[[ [round(x-X0,1),round(y-Y0,1)] for x,y in r] for r in v] for k,v in polys.items()}, open("perims_grid_m.json","w"))
sc=polys["So. Canyon_1994_BLM"]
print("So Canyon rings",len(sc),[len(r) for r in sc])
xs=[x-X0 for r in sc for x,y in r]; ys=[y-Y0 for r in sc for x,y in r]
print("perimeter in grid meters x",min(xs),max(xs),"z",min(ys),max(ys))
# mask
m=Image.new("L",(4096,4096),0); dr=ImageDraw.Draw(m)
for r in sc: dr.polygon([((x-X0)/2,(8192-(y-Y0))/2) for x,y in r],fill=1)
mask=np.array(m).astype(bool); print("mask area ha",mask.sum()*4/1e4, "acres",mask.sum()*4/4046.86)
print("elev in perimeter min/max",el[mask].min(),el[mask].max())
# knob candidates: local maxima within 60 m window, elev 2110-2145, in or within mask
from numpy.lib.stride_tricks import sliding_window_view
s=el[::4,::4]  # 8 m
k=15  # 15*8=120 m window
pad=np.pad(s,k//2,mode='edge'); mx=sliding_window_view(pad,(k,k)).max(axis=(2,3))
cand=np.argwhere((s==mx)&(s>2105)&(s<2150))
ms=mask[::4,::4]
for r,c in cand:
    if ms[max(0,r-12):r+13,max(0,c-12):c+13].any():
        print("knob cand grid x_m",c*8,"z_m",8192-r*8,"elev_m",round(float(s[r,c]),1),"ft",round(float(s[r,c])*3.28084))
# preview
h=Image.open("hill_mosaic.png").convert("RGB"); dr=ImageDraw.Draw(h)
for key,col in (("So. Canyon_1994_BLM",(255,40,40)),("Coal Seam_2002_BLM",(255,170,0))):
    for r in polys[key]: dr.line([((x-X0)/4,(8192-(y-Y0))/4) for x,y in r],fill=col,width=3)
for r,c in cand:
    if ms[max(0,r-12):r+13,max(0,c-12):c+13].any(): dr.ellipse([c*2-6,r*2-6,c*2+6,r*2+6],outline=(0,255,255),width=2)
px,py=utm_fwd(39.5894259,-107.4008876); dr.rectangle([(px-X0)/4-5,(8192-(py-Y0))/4-5,(px-X0)/4+5,(8192-(py-Y0))/4+5],outline=(0,255,0),width=2)
h.save("preview_hill.jpg",quality=85)
Image.open("naip_mosaic.png").resize((2048,2048)).save("preview_naip.jpg",quality=85)
Image.open("topo_mosaic.png").resize((2048,2048)).save("preview_topo.jpg",quality=80)
f=np.array(Image.open("lf_FBFM13.tif")).astype(np.uint8)
pal={2:(200,190,90),5:(150,170,70),8:(40,110,60),9:(110,150,60),10:(20,80,50),12:(120,80,40),91:(150,150,150),93:(220,200,120),98:(60,120,220),99:(200,200,200)}
rgb=np.zeros(f.shape+(3,),np.uint8)
for kk,v in pal.items(): rgb[f==kk]=v
Image.fromarray(rgb).resize((1024,1024),Image.NEAREST).save("preview_fuel.png")
