"""1 config で child の S, D を一様にグリッド探索 (root は model のまま)。"""
import json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from vrm_sim3d import simulate
model, coef, data, k = sys.argv[1:5]
c = np.array(json.load(open(coef))["coef"])
fit_global._init(data)
cfg, tr = fit_global._DATA[int(k)]
n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
S0, D0 = fit_global.MODELS[model][0](c, cfg["pb"], n)
print(cfg["pb"], n, "S0", (S0*L).round(2), "D0", D0.round(3))
best = []
for sc in np.exp(np.linspace(np.log(0.1), np.log(100), 25)):
    for dc in (0.3, 0.5, 0.7, 0.8, 0.9, 0.95, 0.98, 1.0):
        S = S0.copy(); D = D0.copy(); S[1:] = S0[1] * sc; D[1:] = dc
        te, ad = [], []
        for drv, pb in tr:
            v = simulate(drv, n, L, S * L, D)
            cc = np.clip(np.einsum("ntk,ntk->nt", v, pb), -1, 1)
            te.append(np.sqrt(np.mean(np.degrees(np.arccos(cc)) ** 2, axis=1)))
            ad.append(np.abs(fit_global.amp(v) - fit_global.amp(pb)))
        te, ad = np.array(te), np.array(ad)
        best.append((te.mean(), ad.mean(), sc, dc, te.mean(0).round(1)))
best.sort(key=lambda x: x[0])
for b in best[:6]: print("traj %.2f amp %.2f Sx %.2f D %.2f" % b[:4], b[4])
best.sort(key=lambda x: x[1])
for b in best[:4]: print("traj %.2f amp %.2f Sx %.2f D %.2f" % b[:4], b[4])
