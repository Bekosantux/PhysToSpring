import json, sys, numpy as np
from fit_global import Objective, MODELS
if __name__ == "__main__":
    model, path, coef = sys.argv[1], sys.argv[2], json.load(open(sys.argv[3]))["coef"]
    v = json.load(open(path)); cfgs = v["configs"]
    o = Objective(path, 8)
    e0 = o.errors(model, np.zeros(len(coef))); e1 = o.errors(model, coef)
    for i in np.argsort(-(e1.mean(1) - e0.mean(1)))[:8]:
        c = cfgs[i]; S, D = MODELS[model][0](np.array(coef), c["pb"], c["rig"]["segments"])
        print(c["name"], {k: c["pb"][k] for k in ("pull", "spring", "stiffness")}, c["rig"], "base %.2f fit %.2f" % (e0[i].mean(), e1[i].mean()),
              np.round(e1[i], 1).tolist(), "S/L %.1f/%.1f keep %.3f/%.3f" % (S[0], S[1], 1 - D[0], 1 - D[1]))
