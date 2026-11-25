import subprocess, numpy as np, time, sys, os
from PIL import Image
from geo import X0,Y0
N=4096; base="https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage"
def fetch(px0,py0,T,fn,tries=3):
    # px0,py0 = pixel offset from NW corner; T pixels at 2 m
    if os.path.exists(fn):
        try:
            a=np.array(Image.open(fn))
            if a.shape==(T,T) and a.dtype==np.float32: return a
        except Exception: pass
    x0=X0+px0*2; x1=x0+T*2; ytop=Y0+8192-py0*2; ybot=ytop-T*2
    url=f"{base}?bbox={x0},{ybot},{x1},{ytop}&bboxSR=26913&imageSR=26913&size={T},{T}&format=tiff&pixelType=F32&interpolation=RSP_BilinearInterpolation&f=image"
    for k in range(tries):
        subprocess.run(["curl","-sS","-m","45","-o",fn,url])
        try:
            a=np.array(Image.open(fn)); assert a.shape==(T,T) and a.dtype==np.float32; return a
        except Exception: time.sleep(2)
    return None
out=np.full((N,N),np.nan,np.float32); t0=time.time(); missing=[]
for ty in range(4):
    for tx in range(4):
        if time.time()-t0>140: missing.append((ty,tx,'time')); continue
        a=fetch(tx*1024,ty*1024,1024,f"e_{ty}_{tx}.tif",tries=1)
        if a is None:
            ok=True
            for sy in range(2):
                for sx in range(2):
                    b=fetch(tx*1024+sx*512,ty*1024+sy*512,512,f"e_{ty}_{tx}_{sy}{sx}.tif")
                    if b is None: ok=False; missing.append((ty,tx,sy,sx))
                    else: out[ty*1024+sy*512:ty*1024+(sy+1)*512, tx*1024+sx*512:tx*1024+(sx+1)*512]=b
        else: out[ty*1024:(ty+1)*1024, tx*1024:(tx+1)*1024]=a
np.save("elev_north_up.npy",out)
print("missing",missing); print("min",np.nanmin(out),"max",np.nanmax(out),"nan",int(np.isnan(out).sum()))
