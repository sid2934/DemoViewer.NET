import os
import pickle,collections,sys
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
from cur import *
rows,demos=pickle.load(open('/tmp/gv2/py/rows.pkl','rb'))
seen=set();keep=set()
for d in sorted(demos,key=lambda d:(d['path'] or '').lower()):
    if d['sha'] and d['sha'] in seen: continue
    if d['sha']: seen.add(d['sha'])
    keep.add(d['key'])
rows=[g for g in rows if g['demo'] in keep]
tot=collections.Counter()
for M in ['de_mirage','de_inferno','de_dust2','de_nuke','de_ancient','de_anubis','de_overpass','de_train','de_vertigo']:
  for K in ['Smoke','Flash','Molotov','He']:
    gs=indexed([g for g in rows if g['map']==M and g['kind']==K])
    lands=cluster(gs)
    shown={} ;allshown=0;groups=0
    for g in lands:  # VM order: most thrown first
        ls=[s for s in g['lineups'] if len(s['throws'])>=2]
        if not ls: continue
        groups+=1;n=sum(len(s['throws']) for s in ls);allshown+=n
        shown[(g['kind'],g['cell'])]=(n,g)
    vis=sum(v[0] for v in shown.values())
    lost=groups-len(shown)
    tot['groups']+=groups;tot['vis_groups']+=len(shown);tot['throws']+=allshown;tot['vis']+=vis
    if K=='Smoke': print(f"{M:11} {K:6} landing groups shown-worthy {groups:4} visible {len(shown):4} ; lineup throws {allshown:5} visible {vis:5} ({100*vis/max(1,allshown):.0f}%)")
print(tot)
