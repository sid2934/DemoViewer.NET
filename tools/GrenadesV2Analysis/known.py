import os
import pickle,collections,sys,math
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
import cur,dens
from cur import origin,landing,indexed
rows,demos=pickle.load(open('/tmp/gv2/py/rows.pkl','rb'))
seen=set();keep=set()
for d in sorted(demos,key=lambda d:(d['path'] or '').lower()):
    if d['sha'] and d['sha'] in seen: continue
    if d['sha']: seen.add(d['sha'])
    keep.add(d['key'])
rows=[g for g in rows if g['demo'] in keep]
def near(c,r): return lambda p: math.hypot(p[0]-c[0],p[1]-c[1])<=r
def box(x0,y0,x1,y1): return lambda p: x0<=p[0]<=x1 and y0<=p[1]<=y1
W=[('de_mirage','Window (SnipersNest) from T spawn',near((-1176,-630),160),box(1100,-1300,1500,600)),
   ('de_mirage','Top-mid from T spawn',near((20,-473),160),box(1100,-1300,1500,600)),
   ('de_mirage','Stairs',near((-486,-1582),160),box(600,-2500,1600,600)),
   ('de_mirage','CT from T roof / Palace alley',near((-874,-2402),160),box(700,-1400,1500,-600)),
   ('de_mirage','Jungle',box(-1278,-1525,-832,-1192),box(0,-2500,1600,600)),
   ('de_inferno','B CT from Banana',near((976,2702),160),box(-79,943,1037,2604)),
   ('de_inferno','Mid from T spawn',near((1154,525),160),box(-1728,-420,-862,802)),
   ('de_dust2','Xbox from T spawn',near((-318,1427),160),box(-2000,-1400,500,-900)),
   ('de_dust2','CT mid / mid doors',near((-413,1618),160),box(-2000,-1400,500,0)),
   ('de_dust2','A cross from Long doors',near((1187,2166),160),box(518,218,1280,1208)),
   ('de_nuke','Outside, garage from T',near((1579,-1917),160),box(-2500,-2000,-500,0)),
   ('de_nuke','Outside, mid wall from T',near((1134,-2093),160),box(-2500,-2000,-500,0)),
   ('de_ancient','Mid from T spawn',near((-514,243),160),box(-1200,-2600,200,-1800)),
   ('de_anubis','Canal from T',near((-404,111),160),box(-800,-2200,400,-1500))]
cache={}
def grouped(M):
    if M in cache: return cache[M]
    gs=indexed([g for g in rows if g['map']==M and g['kind']=='Smoke'])
    old=cur.cluster(gs);new=dens.cluster(gs)
    # old UI key: last (smallest) shown-worthy cluster per (kind, seed cell) wins
    vis={}
    for g in old:
        if any(len(s['throws'])>=2 for s in g['lineups']): vis[(g['kind'],g['cell'])]=g
    visible=set(id(g) for g in vis.values())
    def index(lands,v=None):
        w={}
        for gi,g in enumerate(lands):
            for s in g['lineups']:
                for t in s['throws']: w[id(t)]=(gi,id(s),len(s['throws']),(v is None) or id(g) in v)
        return w
    cache[M]=(gs,index(old,visible),index(new))
    return cache[M]
print('| Lineup | Map | Throws | Today: shown on map | Today: groups / positions (>=2) / largest | Density: groups / lineups (>=2) / largest |')
print('|---|---|---|---|---|---|')
for M,name,lf,of in W:
    gs,o,n=grouped(M)
    ts=[t for t in gs if lf(landing(t)) and of(origin(t))]
    if not ts: print(M,name,'none');continue
    shown=sum(1 for t in ts if o[id(t)][3] and o[id(t)][2]>=2)
    def summ(w):
        groups=len(set(w[id(t)][0] for t in ts));ls=collections.Counter(w[id(t)][1] for t in ts)
        big=sum(1 for k,v in ls.items() if v>=2);largest=max(w[id(t)][2] for t in ts)
        return f"{groups} / {big} / {largest}"
    print(f"| {name} | {M} | {len(ts)} | {shown} ({100*shown/len(ts):.0f}%) | {summ(o)} | {summ(n)} |")
