# REVIEW MODEL ONLY: two moving disks on a flat plane, no C#/Unity/native-container execution.
import math
import json
SKIN=.0002

def add(a,b): return(a[0]+b[0],a[1]+b[1])
def sub(a,b): return(a[0]-b[0],a[1]-b[1])
def mul(a,t): return(a[0]*t,a[1]*t)
def dot(a,b): return a[0]*b[0]+a[1]*b[1]
def length(a): return math.hypot(*a)
def sweep(a0,a1,b0,b1):
    q=sub(b0,a0); v=sub(sub(b1,b0),sub(a1,a0)); aa=dot(v,v); cc=dot(q,q)-.36
    lo,hi=0,1
    if aa<1e-20:
        if cc>0:return None
    else:
        bb=dot(q,v); d=bb*bb-aa*cc
        if d<0:return None
        lo=max(lo,(-bb-math.sqrt(d))/aa);hi=min(hi,(-bb+math.sqrt(d))/aa)
        if lo>hi:return None
    c=add(q,mul(v,max(0,lo)));mag=length(c);n=mul(c,1/mag) if mag else (0,1)
    depth=.6-dot(sub(b1,a1),n)
    return (n,depth) if depth>SKIN else None

def simulate(lateral, phases=False):
    a,b=(0.,0.),(.6,0.);log=[]
    for tick in range(30):
        starta,startb=a,b;distance=4/60*(.5 if tick<13 else .25) if phases else 4/60
        a=add(a,(distance,0));used=0;attempts=0
        for it in range(48):
            contact=sweep(starta,a,startb,b)
            if not contact:break
            n,depth=contact
            if lateral and attempts<4 and b[0]>a[0]:
                amount=min(depth*5/6,max(0,.05-used))
                if amount>SKIN:
                    sign=1 if b[1]-a[1]>SKIN else -1
                    b=add(b,(0,sign*amount));used+=amount;attempts+=1
                    contact=sweep(starta,a,startb,b)
            if contact:
                n,depth=contact;a=add(a,mul(n,-depth/6));b=add(b,mul(n,depth*5/6))
        assert length(sub(b,a))>=.6-SKIN-1e-9
        advance=a[0]-starta[0]
        if phases: assert distance*5/6-SKIN<=advance<=distance+SKIN, (tick,advance,distance)
        log.append((a,b,it+1))
    return {'a':a,'b':b,'passed':a[0]>b[0],'distance':a[0]}

if __name__=='__main__':
    old = simulate(False)
    current = simulate(True)
    phases = simulate(True, True)
    assert not old['passed'] and current['passed']
    print(json.dumps({'scope':'ILLUSTRATIVE TWO-DISK MODEL; no C#/Unity/native execution; not an independent oracle for the C# solver',
                      'old_normal_only':old, 'bounded_lateral':current, 'phase_arithmetic':phases}, indent=2))
