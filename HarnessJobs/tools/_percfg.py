import json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from vrm_sim3d import simulate, seg_err
model, coef, data = sys.argv[1:4]
c = np.array(json.load(open(coef))["coef"])
fit_global._init(data)
rows = []
for cfg, tr in fit_global._DATA:
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    S, D = fit_global.MODELS[model][0](c, cfg["pb"], n)
    e = [seg_err(simulate(drv, n, L, S * L, D), pb) for drv, pb in tr]
    p = cfg["pb"]
    rows.append((np.mean(e), n, p["pull"], p["spring"], p.get("stiffness", 0), (S*L)[1], D[1]))
for r in sorted(rows):
    print("err %5.1f n %2d pull %.2f spr %.2f k %.2f | S %.1f D %.3f" % r)
