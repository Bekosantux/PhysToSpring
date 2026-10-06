"""1 config について joint ごとの S, D を自由に最適化し、VRM 構造で到達できる誤差の下限を見る。"""
import json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from vrm_sim3d import simulate
from scipy.optimize import minimize
model, coef, data = sys.argv[1:4]
idx = [int(x) for x in sys.argv[4].split(",")]
c = np.array(json.load(open(coef))["coef"])
fit_global._init(data)
cases = json.load(open(data, encoding="utf-8"))["cases"]
def errs(S, D, cfg, tr):
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    out = []
    for drv, pb in tr:
        v = simulate(drv, n, L, S * L, D)
        cc = np.clip(np.einsum("ntk,ntk->nt", v, pb), -1, 1)
        out.append(np.sqrt(np.mean(np.degrees(np.arccos(cc)) ** 2, axis=1)))
    return np.array(out)  # cases x n
for k in idx:
    cfg, tr = fit_global._DATA[k]
    n = cfg["rig"]["segments"]
    S0, D0 = fit_global.MODELS[model][0](c, cfg["pb"], n)
    e0 = errs(S0, D0, cfg, tr)
    x0 = np.r_[np.log(S0), np.log(1 - D0 + 1e-6) * 0 + np.log(np.clip(D0, 1e-4, 1))]
    def f(x):
        S = np.exp(x[:n]); D = np.clip(np.exp(x[n:]), 0, 1)
        return errs(S, D, cfg, tr).mean()
    r = minimize(f, x0, method="Powell", options={"maxfev": 4000, "xtol": 1e-3, "ftol": 1e-5})
    S = np.exp(r.x[:n]); D = np.clip(np.exp(r.x[n:]), 0, 1)
    e1 = errs(S, D, cfg, tr)
    print(cfg["pb"], n)
    print("  model  ", e0.mean().round(2), "depth", e0.mean(0).round(1))
    print("  free   ", e1.mean().round(2), "depth", e1.mean(0).round(1))
    print("  S ratio", (S / S0).round(2))
    print("  D      ", D0.round(3), "->", D.round(3))
