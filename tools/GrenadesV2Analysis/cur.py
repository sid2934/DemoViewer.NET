# Faithful port of GrenadeIndex.Cluster (feature/strat-book 4cf6a6b1)
import math,hashlib,collections
LCS=256.;LCH=128.;OR=16.
OMR=16.;OMH=24.;LLR=128.;LLH=96.;AIM=2.;LMR=96.;LMH=96.
def origin(g):
    return g.get('releasePosition') or g.get('throwerPositionAtSpawn')
def landing(g): return g.get('detonationPosition')
def indexed(rows):
    return [g for g in rows if origin(g) and landing(g)]
def floor(v): return int(math.floor(v))
def rnd(v): return int(math.floor(abs(v)+0.5))*(1 if v>=0 else -1)
def cell(p): return (floor(p[0]/LCS),floor(p[1]/LCS),floor(p[2]/LCH))
def rorig(p): return tuple(rnd(v/OR) for v in p)
def lid(m,k,c,r,j):
    s=f"{m.lower()}|{k}|{c[0]},{c[1]},{c[2]}|{r[0]},{r[1]},{r[2]}|{j}"
    return hashlib.sha256(s.encode()).hexdigest()[:32]
def mean(ps):
    ps=list(ps);n=len(ps)
    return [sum(p[i] for p in ps)/n for i in range(3)] if n else [0,0,0]
def near(a,b,r,h): return math.hypot(a[0]-b[0],a[1]-b[1])<=r and abs(a[2]-b[2])<=h
def aimnear(ya,yb,pa,pb,tol=AIM):
    if ya is not None and yb is not None and abs(((ya-yb)%360+540)%360-180)>tol: return False
    return pa is None or pb is None or abs(pa-pb)<=tol
def centre(ts):
    ys=[t['releaseEyeYaw'] for t in ts if t.get('releaseEyeYaw') is not None]
    ps=[t['releaseEyePitch'] for t in ts if t.get('releaseEyePitch') is not None]
    y=None if not ys else math.degrees(math.atan2(sum(math.sin(math.radians(v)) for v in ys),sum(math.cos(math.radians(v)) for v in ys)))
    p=None if not ps else sum(ps)/len(ps)
    return mean(origin(t) for t in ts),mean(landing(t) for t in ts),y,p
def bucket(p,s): return (floor(p[0]/s),floor(p[1]/s))
def cluster(gs):
    groups=collections.OrderedDict()
    for g in gs:
        k=(g['kind'],cell(landing(g)),rorig(origin(g)),g['jumpThrow'])
        groups.setdefault(k,[]).append(g)
    grid=[]
    for k,ts in groups.items():
        grid.append(dict(kind=k[0],cell=k[1],jump=k[3],id=lid(ts[0]['map'],k[0],k[1],k[2],k[3]),throws=ts))
    grid.sort(key=lambda p:(-len(p['throws']),p['id']))
    seeds=[];sc={}
    for p in grid:
        o,l,y,pi=centre(p['throws']);cx,cy=bucket(o,OMR);home=None
        for dx in (-1,0,1):
            for dy in (-1,0,1):
                for s in sc.get((p['kind'],p['jump'],cx+dx,cy+dy),[]):
                    if near(s['o'],o,OMR,OMH) and near(s['l'],l,LLR,LLH) and aimnear(s['y'],y,s['p'],pi):
                        if home is None or s['order']<home['order']: home=s
                        break
        if home is None:
            s=dict(kind=p['kind'],jump=p['jump'],cell=p['cell'],id=p['id'],o=o,l=l,y=y,p=pi,throws=list(p['throws']),aliases=[p['id']],order=len(seeds))
            seeds.append(s);sc.setdefault((p['kind'],p['jump'],cx,cy),[]).append(s)
        else:
            home['throws']+=p['throws'];home['aliases'].append(p['id'])
    lands=[];lc={}
    for s in sorted(seeds,key=lambda s:(-len(s['throws']),s['id'])):
        l=mean(landing(t) for t in s['throws']);cx,cy=bucket(l,LMR);home=None
        for dx in (-1,0,1):
            for dy in (-1,0,1):
                for g in lc.get((s['kind'],cx+dx,cy+dy),[]):
                    if near(g['l'],l,LMR,LMH):
                        if home is None or g['order']<home['order']: home=g
                        break
        if home is None:
            g=dict(kind=s['kind'],cell=s['cell'],l=l,lineups=[s],order=len(lands));lands.append(g);lc.setdefault((s['kind'],cx,cy),[]).append(g)
        else: home['lineups'].append(s)
    for g in lands:
        g['lineups'].sort(key=lambda s:-len(s['throws']))
        g['n']=sum(len(s['throws']) for s in g['lineups'])
    lands.sort(key=lambda g:-g['n'])
    return lands
