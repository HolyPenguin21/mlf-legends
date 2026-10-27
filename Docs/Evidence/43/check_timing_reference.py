"""Independent numerical model check only: does not execute C# or Unity."""
import json
from pathlib import Path
import numpy as np
from scipy.interpolate import PchipInterpolator,CubicHermiteSpline
from scipy.optimize import brentq
rng=np.random.default_rng(43)
max_error=0.0
max_scaling_error=0.0
segments_checked=0
for case in range(180):
    count=int(rng.integers(2,8))
    x=np.r_[0,np.sort(rng.uniform(.02,.98,count-2)),1].astype(np.float32).astype(float)
    v=np.exp(rng.uniform(-2,2,count)).astype(np.float32).astype(float)
    slopes=PchipInterpolator(x,v).derivative()(x).astype(np.float32).astype(float)
    ref=CubicHermiteSpline(x,v,slopes)
    integral=ref.antiderivative()
    total_ref=float(integral(1)-integral(0))
    frames=int(rng.integers(2,25))
    duration=float(np.exp(rng.uniform(-3,9)))
    baseline=None
    for scale in [1.,1e-25,1e-10,1e10,1e25]:
        vv=v*scale;dd=slopes*scale
        amplitude=max(max(abs(vv)),max(abs(dd)))
        values=vv/amplitude;tangents=dd/amplitude
        segments=[];total=0.
        for i in range(count-1):
            width=x[i+1]-x[i]
            a,b=values[i:i+2];m0,m1=width*tangents[i],width*tangents[i+1]
            A=2*a-2*b+m0+m1;B=-3*a+3*b-2*m0-m1;C=m0;D=a
            def area(u,A=A,B=B,C=C,D=D,width=width):
                return width*u*(D+u*(C/2+u*(B/3+u*A/4)))
            segments.append((x[i],width,total,area))
            total+=area(1.)
            # Independent value and area from SciPy against explicit cubic/Horner expression.
            for u in [0.,.125,.5,.875,1.]:
                actual=((A*u+B)*u+C)*u+D
                expected=float(ref(x[i]+width*u))*scale/amplitude
                assert abs(actual-expected)<1e-11
            segments_checked+=1
        def area_at(t):
            for start,width,before,area in segments:
                if t>start+width: continue
                return before+area(max(0.,min(1.,(t-start)/width)))
            return total
        starts=[]
        for f in range(1,frames):
            target=total*f/frames
            lo,hi=0.,1.
            for _ in range(80):
                mid=(lo+hi)/2
                if area_at(mid)>=target:hi=mid
                else:lo=mid
            start=hi*duration;starts.append(start)
            expected=brentq(lambda t:float(integral(t)-integral(0))-total_ref*f/frames,0,1,xtol=1e-14)*duration
            max_error=max(max_error,abs(start-expected))
        if baseline is None:baseline=np.array(starts)
        else:max_scaling_error=max(max_scaling_error,float(np.max(abs(np.array(starts)-baseline))))
assert max_error<.0001 and max_scaling_error<.0001
result={'check':'independent numerical model of source Hermite formula and boundary inversion against SciPy; not C#/Unity execution','seed':43,'curves':180,'amplitude_scales':[1,1e-25,1e-10,1e10,1e25],'segments_checked':segments_checked,'max_boundary_error_seconds':max_error,'max_scaling_error_seconds':max_scaling_error,'limit_seconds':.0001,'result':'PASS'}
Path(__file__).with_name('timing-reference-check.json').write_text(json.dumps(result,indent=2)+'\n')
print(json.dumps(result))
