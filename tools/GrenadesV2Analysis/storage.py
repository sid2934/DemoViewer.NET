import pickle,gzip,json,struct,sys,collections,glob,os
sys.path.insert(0,os.path.dirname(os.path.abspath(__file__)))
import dens
from cur import origin,landing,indexed
rows,demos=pickle.load(open('/tmp/gv2/py/rows.pkl','rb'))
C='/tmp/gv2/cache/demos'
print('demos',len(demos),'grenades',len(rows))
print('today gz rows %.1f MB, gz paths %.1f MB, raw rows %.1f MB, raw paths %.1f MB'%(sum(d['bytes'] for d in demos)/1e6,sum(d['pbytes'] for d in demos)/1e6,sum(d['raw'] for d in demos)/1e6,sum(d['praw'] for d in demos)/1e6))
KIND={'Smoke':0,'Molotov':1,'Incendiary':2,'He':3,'Flash':4,'Decoy':5}
STR={'Full':0,'Half':1,'Underhand':2,'Other':3,'Unknown':4}
MOV={'Stationary':0,'Walking':1,'Running':2,'Unknown':3}
def q(v): return max(-32768,min(32767,int(round(v/2)))) if v is not None else -32768   # 2-unit grid
def a(v): return int(round(v*100)) if v is not None else -32768
# one compact throw record: what the per-throw consumers read (Opening Tendencies, mining, instance list, watch seek, clips)
FMT='<HhiiBBb3h3h3hhhiBB'   # id-serial idx, round, release tick, detonation tick, kind, flags, slot, origin, landing, spawn, pitch, yaw, steam idx, strength|move, team
print('record bytes',struct.calcsize(FMT))
tot_bin=0;tot_gz=0
for d in demos:
    rs=[g for g in rows if g['demo']==d['key']] if False else None
bydemo=collections.defaultdict(list)
for g in rows: bydemo[g['demo']].append(g)
for d in demos:
    steam={};buf=bytearray()
    for g in bydemo[d['key']]:
        o=origin(g) or [None]*3;l=landing(g) or [None]*3;s=g.get('spawnPosition') or [None]*3
        st=steam.setdefault(g.get('throwerSteamId64'),len(steam))
        flags=(1 if g['jumpThrow'] else 0)|(2 if g.get('releaseCrouched') else 0)|(4 if g.get('releaseOnGround') else 0)
        idx=int(g['id'][1:].split('-')[0])
        buf+=struct.pack(FMT,idx,g['roundNumber'],g['releaseTick'],g.get('detonationTick') or -1,KIND[g['kind']],flags,g['throwerSlot'],
            q(o[0]),q(o[1]),q(o[2]),q(l[0]),q(l[1]),q(l[2]),q(s[0]),q(s[1]),q(s[2]),a(g.get('releaseEyePitch')) if g.get('releaseEyePitch') is not None else -32768,
            a((g.get('releaseEyeYaw') or 0)/2),st,STR.get(g['throwStrengthClass'],4)*4+MOV.get(g['movement'],3),g['throwerTeam'])
    buf+=b''.join(int(k or 0).to_bytes(8,'little') for k in steam)
    tot_bin+=len(buf);tot_gz+=len(gzip.compress(bytes(buf)))
print('compact throw log: raw %.2f MB, gz %.2f MB'%(tot_bin/1e6,tot_gz/1e6))
# lineup store: density lineups >=2 per map+kind, each with anchor, technique, counts, stats, a representative trajectory
lineups=0;members=0;single=0
for m in sorted(set(d['map'] for d in demos if d['map'])):
    gs=indexed([g for g in rows if g['map']==m])
    for grp in dens.cluster(gs):
        for l in grp['lineups']:
            if len(l['throws'])>=2: lineups+=1;members+=len(l['throws'])
            else: single+=1
# representative trajectory size: mean raw path bytes per grenade
avg_path=sum(d['praw'] for d in demos)/len(rows)
lineup_raw=lineups*(16+8+2*3*4+60+avg_path)   # id, counts, anchors, stats, one path
print('lineups>=2',lineups,'members',members,'single throws',single,'avg raw path bytes/grenade %.0f'%avg_path)
print('lineup store raw %.2f MB (gz ~%.2f MB at the paths ratio)'%(lineup_raw/1e6,lineup_raw/1e6*sum(d['pbytes'] for d in demos)/sum(d['praw'] for d in demos)))
