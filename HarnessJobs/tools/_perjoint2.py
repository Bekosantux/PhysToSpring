"""1 config について joint ごとの S, D を自由に最適化 (目的: 軌跡誤差 + w 振れ幅差)。S は model 比、D は sigmoid。"""
import json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from vrm_sim3d import simulate
from scipy.optimize import minimize
model, coef, data = sys.argv[1:4]
idx = [int(x) for x in sys.argv[4].split(",")]
w = float(sys.argv[5])
c = np.array(json.load(open(coef))["coef"])
fit_global._init(data)
def ev(S, D, cfg, tr):
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    te, ae, ap, av = [], [], [], []
    for drv, pb in tr:
        v = simulate(drv, n, L, S * L, D)
        cc = np.clip(np.einsum("ntk,ntk->nt", v, pb), -1, 1)
        te.append(np.sqrt(np.mean(np.degrees(np.arccos(cc)) ** 2, axis=1)))
        ap.append(fit_global.amp(pb)); av.append(fit_global.amp(v))
    return np.array(te), np.array(ap), np.array(av)
sig = lambda y: 1 / (1 + np.exp(-y))
for k in idx:
    cfg, tr = fit_global._DATA[k]
    n = cfg["rig"]["segments"]
    S0, D0 = fit_global.MODELS[model][0](c, cfg["pb"], n)
    D0c = np.clip(D0, 1e-3, 1 - 1e-3)
    y0 = np.log(D0c / (1 - D0c))
    def unpack(x): return S0 * np.exp(x[:n]), sig(y0 + x[n:])
    def f(x):
        te, ap, av = ev(*unpack(x), cfg, tr)
        return te.mean() + w * np.abs(ap - av).mean()
    r = minimize(f, np.zeros(2 * n), method="Powell", options={"maxfev": 6000, "xtol": 1e-3, "ftol": 1e-5})
    for nm, x in (("model", np.zeros(2 * n)), ("free", r.x)):
        te, ap, av = ev(*unpack(x), cfg, tr)
        print(f"  {nm:5s} traj {te.mean():.2f} ampdiff {np.abs(ap-av).mean():.2f} | traj/depth {te.mean(0).round(1)}")
        print(f"        amp PB  {ap.mean(0).round(1)}\n        amp VRM {av.mean(0).round(1)}")
    S, D = unpack(r.x)
    print(cfg["pb"], n, "\n  S ratio", (S / S0).round(2), "\n  D", D0.round(3), "->", D.round(3), flush=True)
