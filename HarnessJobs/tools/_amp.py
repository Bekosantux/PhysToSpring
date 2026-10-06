import json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from vrm_sim3d import simulate
model, coef, data = sys.argv[1:4]
c = np.array(json.load(open(coef))["coef"])
fit_global._init(data)
cases = json.load(open(data.split(",")[0], encoding="utf-8"))["cases"]
def amp(x):  # (n,T,3) -> per seg rms angle from frame 0
    d = np.clip(np.einsum("ntk,nk->nt", x, x[:, 0]), -1, 1)
    return np.sqrt(np.mean(np.degrees(np.arccos(d)) ** 2, axis=1))
acc = {}
for cfg, tr in fit_global._DATA:
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    if n < 9: continue
    S, D = fit_global.MODELS[model][0](c, cfg["pb"], n)
    for cn, (drv, pb) in zip(cases, tr):
        v = simulate(drv, n, L, S * L, D)
        acc.setdefault(cn, []).append((amp(pb)[:9], amp(v)[:9]))
for cn, l in acc.items():
    a = np.array(l)  # m,2,9
    print(f"{cn:12s} PB ", " ".join(f"{x:5.1f}" for x in a[:, 0].mean(0)))
    print(f"{'':12s} VRM", " ".join(f"{x:5.1f}" for x in a[:, 1].mean(0)))
