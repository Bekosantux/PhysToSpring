"""
写像の誤差を joint の深さ (チェーン先頭からの番号) ごとに集計する (vrm_sim3d, Unity 不要)。
使い方: python depth_err.py MODEL COEF.json DATA.json[,DATA2.json] [--workers 8]
"""
import argparse, json, os, sys
from multiprocessing import Pool
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
import fit_global
from vrm_sim3d import simulate


def seg_errs(a, b):
    c = np.clip(np.einsum("ntk,ntk->nt", a, b), -1, 1)
    return np.sqrt(np.mean(np.degrees(np.arccos(c)) ** 2, axis=1))


def _eval(args):
    model, c, i = args
    cfg, data = fit_global._DATA[i]
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    S, D = fit_global.MODELS[model][0](c, cfg["pb"], n)
    return [seg_errs(simulate(drv, n, L, S * L, D), pb) for drv, pb in data]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("coef")
    ap.add_argument("data")
    ap.add_argument("--workers", type=int, default=8)
    a = ap.parse_args()
    c = np.array(json.load(open(a.coef))["coef"])
    nconf = sum(len(json.load(open(p, encoding="utf-8"))["configs"]) for p in a.data.split(","))
    cases = json.load(open(a.data.split(",")[0], encoding="utf-8"))["cases"]
    with Pool(a.workers, initializer=fit_global._init, initargs=(a.data,)) as pool:
        res = pool.map(_eval, [(a.model, c, i) for i in range(nconf)])
    depth = {}
    per_case = {cn: [] for cn in cases}
    for r in res:
        for cn, e in zip(cases, r):
            per_case[cn].append(e.mean())
            for d, v in enumerate(e):
                depth.setdefault(d, []).append(v)
    print("mean", np.mean([v for r in res for e in r for v in [e.mean()]]).round(3))
    print("per case", {k: round(float(np.mean(v)), 2) for k, v in per_case.items()})
    print("per depth", " ".join(f"{d}:{np.mean(v):.1f}" for d, v in sorted(depth.items())))


if __name__ == "__main__":
    main()
