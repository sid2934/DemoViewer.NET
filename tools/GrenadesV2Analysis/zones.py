import json,os
ASSETS=os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','..','assets')
_cache={}
def load(m):
    if m in _cache: return _cache[m]
    try: z=json.load(open(os.path.join(ASSETS,m,'zones.json')))
    except Exception: _cache[m]=None; return None
    names={p['id']:p['name'] for p in z['places']}
    areas=[(names.get(a['place']),a['z'],a['xy']) for a in z['areas']]
    _cache[m]=areas; return areas
def pip(x,y,xy):
    n=len(xy)//2;ins=False;j=n-1
    for i in range(n):
        xi,yi=xy[2*i],xy[2*i+1];xj,yj=xy[2*j],xy[2*j+1]
        if (yi>y)!=(yj>y) and x<(xj-xi)*(y-yi)/(yj-yi+1e-12)+xi: ins=not ins
        j=i
    return ins
def place(m,p):
    # approximate: the area under the point with the closest floor below (or slightly above)
    a=load(m)
    if not a: return None
    best=None
    for n,z,xy in a:
        if pip(p[0],p[1],xy):
            dz=p[2]-z
            if -32<=dz<=160 and (best is None or abs(dz)<best[0]): best=(abs(dz),n)
    return best[1] if best else None
