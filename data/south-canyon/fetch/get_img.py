import subprocess, numpy as np, time, sys, os
from PIL import Image
from geo import X0,Y0
Image.MAX_IMAGE_PIXELS=None
def mosaic(name, base, extra, N, T, res, tries=3, budget=150):
    out=Image.new("RGB",(N,N)); t0=time.time(); missing=[]
    for ty in range(N//T):
        for tx in range(N//T):
            fn=f"{name}_{ty}_{tx}.png"; im=None
            if os.path.exists(fn):
                try: im=Image.open(fn); im.load(); assert im.size==(T,T)
                except Exception: im=None
            if im is None:
                if time.time()-t0>budget: missing.append((ty,tx)); continue
                x0=X0+tx*T*res; x1=x0+T*res; ytop=Y0+8192-ty*T*res; ybot=ytop-T*res
                url=f"{base}?bbox={x0},{ybot},{x1},{ytop}&bboxSR=26913&imageSR=26913&size={T},{T}&{extra}&f=image"
                for k in range(tries):
                    subprocess.run(["curl","-sS","-m","50","-o",fn,url])
                    try: im=Image.open(fn); im.load(); assert im.size==(T,T); break
                    except Exception: im=None; time.sleep(2)
                if im is None: missing.append((ty,tx)); continue
            out.paste(im.convert("RGB"),(tx*T,ty*T))
    return out, missing
if __name__=="__main__":
    which=sys.argv[1]
    if which=="naip":
        out,miss=mosaic("naip","https://imagery.nationalmap.gov/arcgis/rest/services/USGSNAIPImagery/ImageServer/exportImage","format=png&bandIds=0,1,2&interpolation=RSP_BilinearInterpolation",8192,2048,1)
    elif which=="topo":
        out,miss=mosaic("topo","https://basemap.nationalmap.gov/arcgis/rest/services/USGSTopo/MapServer/export","format=png24&dpi=96&transparent=false",4096,2048,2)
    elif which=="hill":
        out,miss=mosaic("hill","https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer/exportImage","format=png&renderingRule=%7B%22rasterFunction%22%3A%22Hillshade%20Multidirectional%22%7D",2048,1024,4)
    print(which,"missing",miss)
    if not miss: out.save(f"{which}_mosaic.png"); print("saved",out.size, np.array(out.resize((64,64))).mean(axis=(0,1)))
