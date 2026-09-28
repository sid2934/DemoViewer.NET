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
def stats(lands):
    worthy=[g for g in lands if any(len(s['throws'])>=2 for s in g['lineups'])]
    ln=[s for g in lands for s in g['lineups']]
    l2=[s for s in ln if len(s['throws'])>=2]
    spread=0
    for g in lands:
        ps=[landing(t) for s in g['lineups'] for t in s['throws']]
        c=cur.mean(ps);spread=max(spread,max(math.hypot(p[0]-c[0],p[1]-c[1]) for p in ps))
    return len(worthy),len(ln),len(l2),sum(len(s['throws']) for s in l2),max(len(s['throws']) for s in ln),spread
maps=sys.argv[1:] or ['de_mirage','de_inferno','de_dust2','de_nuke','de_ancient','de_anubis','de_overpass','de_train']
for M in maps:
    gs=indexed([g for g in rows if g['map']==M and g['kind']=='Smoke'])
    a=stats(cur.cluster(gs));b=stats(dens.cluster(gs))
    print(f"{M:11} smokes {len(gs):5} | grid: groups {a[0]:4} lineups {a[1]:5} >=2 {a[2]:4} covering {a[3]:5} ({100*a[3]/len(gs):.0f}%) max {a[4]:4} | density: groups {b[0]:4} lineups {b[1]:5} >=2 {b[2]:4} covering {b[3]:5} ({100*b[3]/len(gs):.0f}%) max {b[4]:4} spread {b[5]:.0f}")
