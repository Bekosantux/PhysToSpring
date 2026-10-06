"""_constraint.py の重み c_i を式にして train で係数をフィットし、test で汎化を確認する。
c_0 = 1, c_i = clip(1 - k (a + b (i-1)/(n-2)), 0, 1)  (k = PB stiffness)。k < KTH の config は現行 (constraint なし)。
使い方: python _constraint_fit.py MODEL COEF.json TRAIN.json TEST.json"""
import json, sys, numpy as np
from multiprocessing import Pool
sys.path.insert(0, '.')
import fit_global
from _constraint import errs
from scipy.optimize import minimize

_C = None


def _init(path, coef):
    global _C
    fit_global._init(path); _C = coef


def weights(k, n, p):
    i = np.arange(n)
    c = 1 - k * (p[0] + p[1] * np.clip(i - 1, 0, None) / max(n - 2, 1))
    c[0] = 1.0
    return np.clip(c, 0, 1)


def _eval(args):
    model, p, i = args
    cfg, tr = fit_global._DATA[i]
    n, k = cfg["rig"]["segments"], cfg["pb"].get("stiffness", 0.0)
    S, D = fit_global.MODELS[model][0](_C, cfg["pb"], n)
    if p is None:
        return k, errs(cfg, tr, S, D, np.ones(n), 0).mean()
    return k, errs(cfg, tr, S, D, weights(k, n, p), 1).mean()


def run(pool, model, n, p):
    return np.array(pool.map(_eval, [(model, p, i) for i in range(n)]))


def main():
    model, coef, train, test = sys.argv[1:5]
    c = np.array(json.load(open(coef))["coef"])
    ntr = len(json.load(open(train, encoding="utf-8"))["configs"])
    nte = len(json.load(open(test, encoding="utf-8"))["configs"])
    with Pool(14, initializer=_init, initargs=(train, c)) as ptr, Pool(14, initializer=_init, initargs=(test, c)) as pte:
        base_tr = run(ptr, model, ntr, None)
        k_tr = base_tr[:, 0]
        best = None
        for kth in (0.12, 0.15, 0.18, 0.21):
            sel = k_tr >= kth
            f = lambda p: np.where(sel, run(ptr, model, ntr, p)[:, 1], base_tr[:, 1]).mean()
            r = minimize(f, [1.0, 0.5], method="Nelder-Mead", options={"maxfev": 120, "xatol": 1e-2, "fatol": 1e-3})
            print(f"kth {kth}: train {base_tr[:, 1].mean():.2f} -> {r.fun:.2f}  a,b {r.x.round(3)}", flush=True)
            if best is None or r.fun < best[0]: best = (r.fun, kth, r.x)
        _, kth, p = best
        base_te = run(pte, model, nte, None)
        con_te = run(pte, model, nte, p)
        sel = base_te[:, 0] >= kth
        mix = np.where(sel, con_te[:, 1], base_te[:, 1])
        print(f"TEST kth {kth} a,b {p.round(3)}: current {base_te[:, 1].mean():.2f} -> {mix.mean():.2f}"
              f"  (k>=kth {sel.sum()} configs: {base_te[sel, 1].mean():.2f} -> {con_te[sel, 1].mean():.2f})")
        for kk, e0, e1 in sorted(zip(base_te[:, 0], base_te[:, 1], mix)):
            print(f"  k {kk:.3f}  {e0:6.2f} -> {e1:6.2f}")


if __name__ == "__main__":
    main()
