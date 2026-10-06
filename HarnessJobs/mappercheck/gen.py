import sys, numpy as np
sys.path.insert(0, "../tools")
import json, mapping
corr = json.load(open("../maps/lerp_v5.json"))["corr"]
rng = np.random.default_rng(1)
with open("cases.csv", "w") as f:
    for i in range(4000):
        adv = i % 2 == 1
        pb = {"integrationType": "Advanced" if adv else "Simplified", "pull": rng.uniform(0, 1), "spring": rng.uniform(0, 1),
              "stiffness": rng.uniform(0, 1), "gravity": rng.uniform(0, 1) if rng.uniform() < 0.6 else 0.0,
              "gravityFalloff": rng.uniform(0, 1) if rng.uniform() < 0.5 else 0.0}
        L = rng.uniform(0.005, 0.3); root = bool(rng.integers(2))
        a0 = rng.uniform(0, np.pi); segs = 4
        al = mapping.chain_alphas(pb, a0, segs)
        j = rng.integers(segs); a, phi = al[j]
        r = mapping.lerp_model(pb, L, 1/60, corr, a, phi, False, root)
        f.write(",".join(str(x) for x in [int(adv), pb["pull"], pb["spring"], pb["stiffness"], pb["gravity"], pb["gravityFalloff"], L, int(root), a0, j, a, phi, r["stiffness"], r["dragForce"], r["gravityPower"]]) + "\n")
