import os
import pickle,collections,sys,math
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
import cur,dens
from cur import origin,landing,indexed
from zones import place
rows,demos=pickle.load(open('/tmp/gv2/py/rows.pkl','rb'))
seen=set();keep=set()
for d in sorted(demos,key=lambda d:(d['path'] or '').lower()):
    if d['sha'] and d['sha'] in seen: continue
    if d['sha']: seen.add(d['sha'])
    keep.add(d['key'])
rows=[g for g in rows if g['demo'] in keep]
M=sys.argv[1]
gs=indexed([g for g in rows if g['map']==M and g['kind']=='Smoke'])
for g in dens.cluster(gs)[:int(sys.argv[2]) if len(sys.argv)>2 else 14]:
    lp=collections.Counter(place(M,landing(t)) for s in g['lineups'] for t in s['throws']).most_common(1)[0][0]
    tops=[]
    for s in g['lineups'][:4]:
        if len(s['throws'])<2: break
        tops.append(f"{len(s['throws'])}@{place(M,s['o'])}({s['o'][0]:.0f},{s['o'][1]:.0f}){'J' if s['jump'] else ''}{'R' if s['tech'][2]=='run' else ''}")
    print(f"{lp} ({g['l'][0]:.0f},{g['l'][1]:.0f},{g['l'][2]:.0f}) n={g['n']} lineups={len(g['lineups'])}: "+' '.join(tops))
