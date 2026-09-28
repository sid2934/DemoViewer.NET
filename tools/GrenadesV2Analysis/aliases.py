import os,pickle,collections,sys,math
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
import cur,dens
from cur import origin,landing,indexed
rows,demos=pickle.load(open('/tmp/gv2/py/rows.pkl','rb'))
# How many grid lineup ids (today's stored ids) spread over more than one density lineup.
split=0;total=0;split_throws=0
for m in sorted(set(d['map'] for d in demos if d['map'])):
    for k in ('Smoke','Flash','Molotov','Incendiary','He','Decoy'):
        gs=indexed([g for g in rows if g['map']==m and g['kind']==k])
        if not gs: continue
        gid={}
        for g in cur.cluster(gs):
            for s in g['lineups']:
                for t in s['throws']: gid[id(t)]=s['id']
        where=collections.defaultdict(set)
        for g in dens.cluster(gs):
            for i,l in enumerate(g['lineups']):
                for t in l['throws']: where[gid[id(t)]].add(id(l))
        sizes=collections.Counter(gid.values())
        for k2,v in where.items():
            if sizes[k2]<2: continue
            total+=1
            if len(v)>1: split+=1;split_throws+=sizes[k2]
print('grid lineups of 2+:',total,'spread over more than one density lineup:',split,'holding',split_throws,'throws')
# Window from T spawn: demos and place on the 513 radius set
from zones import place
sm=indexed([g for g in rows if g['map']=='de_mirage' and g['kind']=='Smoke'])
w=[g for g in sm if math.hypot(landing(g)[0]+1176,landing(g)[1]+630)<=160 and 1100<=origin(g)[0]<=1500 and -1300<=origin(g)[1]<=600]
print('window set',len(w),'demos',len(set(g['demo'] for g in w)),'SnipersNest',sum(1 for g in w if place('de_mirage',landing(g))=='SnipersNest'),'z>-180',sum(1 for g in w if landing(g)[2]>-180))
