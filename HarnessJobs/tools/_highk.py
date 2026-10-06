import json, sys, numpy as np
from fit_global import Objective
if __name__ == "__main__":
    coef = json.load(open("../maps/coef_adv_rc3.json"))["coef"]
    for path in sys.argv[1:]:
        v = json.load(open(path)); cfgs = v["configs"]; o = Objective(path, 8)
        e0 = o.errors("adv_rc3", np.zeros(14)); e1 = o.errors("adv_rc3", coef)
        ks = np.array([c["pb"]["stiffness"] for c in cfgs]); ps = np.array([c["pb"]["pull"] for c in cfgs])
        print(path, v["cases"], flush=True)
        for name, s in [("k<0.5", ks < .5), ("k>=0.5 p<0.3", (ks >= .5) & (ps < .3)), ("k>=0.5 p>=0.3", (ks >= .5) & (ps >= .3))]:
            print(f" {name} n{s.sum()} base", np.round(e0[s].mean(0), 1), " rc3", np.round(e1[s].mean(0), 1), flush=True)
