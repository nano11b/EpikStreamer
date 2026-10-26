import sys,re,json,html
src=open(sys.argv[1]).read()
if src.lstrip().startswith('{'): d=json.loads(src)
else: d=json.loads(html.unescape(re.search(r'<title>(.*?)</title>',src,re.S).group(1)))
fails=0
def check(ok,what):
    global fails; print(('PASS ' if ok else 'FAIL ')+what); fails+= not ok
S=d['scans']
for s in S: print('   scan: n=%d total=%s complete=%s virtualized=%s avatars=%d colors=%d'%(s['n'],s['total'],s['complete'],s['virtualized'],s['withAvatar'],s['withColor']))
check(d['renderedMax']<=16,f"mock list is virtualized ({d['renderedMax']} rows rendered of 150)")
check(len(S)==4,'4 scans posted')
q,f1,f2,u=S
check(q['n']<=16 and not q['complete'] and q['total']==150,'quick scan: rendered rows only, partial, total read from the "Members 150" heading')
check(f1['n']==150 and f1['complete'] and f1['virtualized'],'full scan scrolls the whole virtualized list: 150/150, complete')
check(len(set(f1['names']))==150,'merged by name, no duplicates')
check(f1['withAvatar']==120 and f1['withColor']==113,'avatars (120) and name colours (113) captured')
sm={x['name']:x for x in f1['sample']}
check(sm['member001']['avatar']=='https://cdn.example.test/avatars/1.png' and sm['member001']['color']=='rgb(96, 165, 250)','avatar URL + computed name colour per member')
check(sm['member004']['avatar']=='' ,'member without an avatar → empty (initials ball)')
check(d['restored'],"the list's scroll position is restored after the scan")
check('member007' not in f2['names'] and 'newbie' in f2['names'] and f2['n']==150 and f2['complete'],'second full scan sees the leave (member007) and the join (newbie)')
check(not u['complete'] and u['n']<=16,"user scrolling → no programmatic scroll (partial read only)")
check(d['avatars']==[{'ok':True,'err':''}],'avatar fetch fallback returns a data: URL')
g=d['diag']; print('   diag:',json.dumps(g))
check(g['verified'] is False and g['listFound'] and g['rowsWithAvatar']>0 and g['totalShown']==150 and 'memberAvatar' in g['selectors'],'ballPit diagnostics: flagged unverified, selectors listed')
check('member0' not in json.dumps(g),'diagnostics contain no names')
print('MEMBERS MOCK PASSED' if fails==0 else f'{fails} FAILED'); sys.exit(1 if fails else 0)
