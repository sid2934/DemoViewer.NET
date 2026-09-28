# Density (leader) grouping prototype for grenades-v2: lineups first (spot + technique + where it lands),
# then landing groups over the lineups' mean landings.
import math,collections
from cur import origin,landing,mean
LINEUP_LAND_R=192; LINEUP_LAND_H=96
ORIG_R={'still':24,'run':64}; ORIG_H=64
GROUP_R={'Smoke':128,'Molotov':96,'Incendiary':96,'He':128,'Flash':160,'Decoy':128}; GROUP_H=96
def tech(g):
    s=g.get('throwStrengthClass'); s=s if s in ('Full','Half','Underhand') else 'Full'
    return (bool(g['jumpThrow']), s, 'run' if g.get('movement')=='Running' else 'still')
def dxy(a,b): return math.hypot(a[0]-b[0],a[1]-b[1])
def leader(items,pos,r,h,key,extra=None,weight=lambda it:1):
    """Density-ordered leader clustering: most crowded point seeds first; each point joins the nearest seed
    within r/h (and extra), never another member, so nothing chains. One refinement pass re-centres."""
    cells=collections.defaultdict(list)
    for it in items: cells[(key(it),math.floor(pos(it)[0]/r),math.floor(pos(it)[1]/r))].append(it)
    def around(p,k,table):
        cx=math.floor(p[0]/r);cy=math.floor(p[1]/r)
        for dx in (-1,0,1):
            for dy in (-1,0,1):
                yield from table.get((k,cx+dx,cy+dy),())
    dens={id(it):sum(weight(o) for o in around(pos(it),key(it),cells) if dxy(pos(it),pos(o))<=r and abs(pos(it)[2]-pos(o)[2])<=h) for it in items}
    order=sorted(items,key=lambda it:(-dens[id(it)],-weight(it),round(pos(it)[0],1),round(pos(it)[1],1),round(pos(it)[2],1)))
    seeds=[];table=collections.defaultdict(list)
    def fits(it,s): return abs(pos(it)[2]-s['c'][2])<=h and (extra is None or extra(it,s))
    for it in order:
        p=pos(it);k=key(it);best=None;bd=1e18
        for s in around(p,k,table):
            dd=dxy(p,s['c'])
            if dd<=r and dd<bd and fits(it,s): best,bd=s,dd
        if best is None:
            s=dict(c=list(p),key=k,m=[it],seed=it);seeds.append(s)
            table[(k,math.floor(p[0]/r),math.floor(p[1]/r))].append(s)
        else: best['m'].append(it)
    return seeds
def wmean(items,pos,w):
    tot=sum(w(i) for i in items);return [sum(pos(i)[k]*w(i) for i in items)/tot for k in range(3)]
def cluster(gs):
    out=[]
    bykind=collections.defaultdict(list)
    for g in gs: bykind[g['kind']].append(g)
    for kind,items in bykind.items():
        lineups=[]
        for spot in ('still','run'):
            sub=[g for g in items if tech(g)[2]==spot]
            land_ok=lambda it,s: dxy(landing(it),landing(s['seed']))<=LINEUP_LAND_R and abs(landing(it)[2]-landing(s['seed'])[2])<=LINEUP_LAND_H
            for s in leader(sub,origin,ORIG_R[spot],ORIG_H,tech,land_ok):
                lineups.append(dict(throws=s['m'],o=mean(origin(t) for t in s['m']),land=mean(landing(t) for t in s['m']),jump=s['key'][0],tech=s['key']))
        r=GROUP_R.get(kind,128)
        groups=leader(lineups,lambda l:l['land'],r,GROUP_H,lambda l:0,weight=lambda l:len(l['throws']))
        for gp in groups:
            gp['c']=wmean(gp['m'],lambda l:l['land'],lambda l:len(l['throws']))
        # centres that ended up within half a radius of a bigger group's centre fold into it
        groups.sort(key=lambda gp:-sum(len(l['throws']) for l in gp['m']))
        kept=[]
        for gp in groups:
            home=next((k for k in kept if dxy(k['c'],gp['c'])<=r/2 and abs(k['c'][2]-gp['c'][2])<=GROUP_H),None)
            if home: home['m']+=gp['m']
            else: kept.append(gp)
        for gp in kept:
            ls=sorted(gp['m'],key=lambda l:-len(l['throws']))
            out.append(dict(kind=kind,l=wmean(ls,lambda l:l['land'],lambda l:len(l['throws'])),lineups=ls,n=sum(len(l['throws']) for l in ls)))
    out.sort(key=lambda g:-g['n'])
    return out
