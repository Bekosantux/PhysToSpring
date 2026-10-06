import json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from vrm_sim3d import simulate
model, coef, data, k, case = sys.argv[1:6]
k = int(k)
c = np.array(json.load(open(coef))["coef"])
fit_global._init(data)
cases = json.load(open(data, encoding="utf-8"))["cases"]
cfg, tr = fit_global._DATA[k]
n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
S, D = fit_global.MODELS[model][0](c, cfg["pb"], n)
drv, pb = tr[cases.index(case)]
v = simulate(drv, n, L, S * L, D)
print(cfg["pb"], n, "S", (S*L).round(2), "D", D.round(3))
d0 = drv["dir"]
def sang(x):  # signed angle in plane of d0 and x-axis-ish; use atan2 of x component vs y
    return np.degrees(np.arctan2(x[..., 0], -x[..., 1])) if abs(d0[1]) > 0.5 else np.degrees(np.arctan2(x[..., 1], x[..., 0]))
print("dir0", d0.round(2))
deps = [0, 1, 2, 4, 6, 8, n - 1]
print("  t  " + " ".join(f"pb{d:<4d}" for d in deps) + " | " + " ".join(f"vr{d:<4d}" for d in deps) + " | drvx")
for t in range(int(sys.argv[6]), int(sys.argv[7])):
    print(f"{t:3d} " + " ".join(f"{sang(pb[d, t]):6.1f}" for d in deps) + " | " + " ".join(f"{sang(v[d, t]):6.1f}" for d in deps) + f" | {drv['pos'][t,0]:.3f}")
