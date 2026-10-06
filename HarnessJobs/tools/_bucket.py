import json, sys, numpy as np
from fit_global import Objective
if __name__ == "__main__":
    model, path, coef = sys.argv[1], sys.argv[2], json.load(open(sys.argv[3]))["coef"]
    cfgs = json.load(open(path))["configs"]
    o = Objective(path, 8)
    e0 = o.errors(model, np.zeros(len(coef))).mean(1); e1 = o.errors(model, coef).mean(1)
    key = sys.argv[4] if len(sys.argv) > 4 else "stiffness"
    ks = np.array([c["pb"][key] for c in cfgs])
    for lo, hi in [(0, .1), (.1, .2), (.2, .3), (.3, .5), (.5, .7), (.7, 1)]:
        s = (ks >= lo) & (ks < hi)
        if not s.any(): continue
        print(f"{key[:2]} {lo:.1f}-{hi:.1f} n {s.sum():2d}  base {e0[s].mean():6.3f}  fit {e1[s].mean():6.3f}  (median {np.median(e1[s]):.3f})")
