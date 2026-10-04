# Grenades v2 analysis. Copy the cache first, never read the live one:
#   rsync -a --include='*/' --include='*.grenades*' --exclude='*' <cache>/demos/ /tmp/gv2/cache/demos/
#   cp <cache>/index.json /tmp/gv2/cache/
# then: python3 load.py; python3 collide.py; python3 compare.py; python3 known.py; python3 storage.py
import json,gzip,os,glob,pickle,collections
C='/tmp/gv2/cache'
idx=json.load(open(C+'/index.json'))['Entries']
byname={os.path.basename(e['Path']):e for e in idx}
rows=[];demos=[]
for f in sorted(glob.glob(C+'/demos/*.grenades.json.gz')):
    d=json.load(gzip.open(f))
    fn=d['demo']['fileName'];e=byname.get(fn)
    m=e['Map'] if e else None
    pf=f.replace('.grenades.json.gz','.grenades.paths.json.gz')
    demos.append(dict(key=d['demo']['stableKey'],file=fn,map=m,sha=(e or {}).get('Sha256'),n=len(d['grenades']),tr=d['clock']['tickRate'],
        bytes=os.path.getsize(f),pbytes=os.path.getsize(pf) if os.path.exists(pf) else 0,
        raw=len(gzip.open(f).read()),praw=len(gzip.open(pf).read()) if os.path.exists(pf) else 0,walker=d['walker']['version'],path=(e or {}).get('Path')))
    for g in d['grenades']:
        g['map']=m;g['demo']=d['demo']['stableKey'];g['file']=fn;rows.append(g)
pickle.dump((rows,demos),open('/tmp/gv2/py/rows.pkl','wb'))
print(len(rows),len(demos),collections.Counter(x['map'] for x in demos))
print(collections.Counter(x['walker'] for x in demos))
print('gz rows',sum(x['bytes'] for x in demos),'gz paths',sum(x['pbytes'] for x in demos),'raw rows',sum(x['raw'] for x in demos),'raw paths',sum(x['praw'] for x in demos))
