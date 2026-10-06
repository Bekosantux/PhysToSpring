"""
collect.py のトレースに対して VRM パラメータをオフラインで最適化する (vrm_sim3d)。
  percfg: 設定ごとに 先頭 joint / 子 joint の (stiffness, drag) を最適化
"""
import argparse, json, os, sys
from concurrent.futures import ProcessPoolExecutor
import numpy as np
from scipy.optimize import minimize
sys.path.insert(0, os.path.dirname(__file__))
from vrm_sim3d import load_trace, simulate, seg_err


def load_set(path):
    v = json.load(open(path, encoding="utf-8"))
    return v


def load_cfg_data(rd, cfg, cases):
    n = cfg["rig"]["segments"]
    return [load_trace(rd, f"{cfg['name']}__{c}", n)[:2] for c in cases]


def case_errors(data, n, L, S, D, G=0.0, gdir=(0, -1, 0)):
    return [seg_err(simulate(drv, n, L, S, D, G, gdir), pb) for drv, pb in data]


def rootchild(x, n, L):
    """x = [log(S_r/L), log(S_c/L), keep_r, keep_c] -> joint 配列 (stiffness は L に比例)。"""
    S = np.r_[np.exp(x[0]), np.full(n - 1, np.exp(x[1]))] * L
    k = np.clip(np.r_[x[2], np.full(n - 1, x[3])], 0, 1)
    return S, 1 - k


def percfg(args):
    rd, cfg, cases, x0s = args
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    data = load_cfg_data(rd, cfg, cases)
    f = lambda x: float(np.mean(case_errors(data, n, L, *rootchild(x, n, L)))) + 10 * float(np.sum(np.clip(x[2:], None, 0) ** 2 + np.clip(x[2:] - 1, 0, None) ** 2))
    best = None
    for x0 in x0s:
        r = minimize(f, x0, method="Nelder-Mead", options={"xatol": 1e-3, "fatol": 1e-4, "maxfev": 600})
        if best is None or r.fun < best.fun:
            best = r
    S, D = rootchild(best.x, n, L)
    return {"name": cfg["name"], "pb": cfg["pb"], "rig": cfg["rig"], "x": best.x.tolist(),
            "S": (S / L * 0.05).tolist(), "D": D.tolist(), "err": case_errors(data, n, L, S, D)}


def starts(pb, L):
    """初期値: Advanced の閉形式 + いくつかの変形。"""
    p, m, k = pb["pull"], pb["spring"], pb.get("stiffness", 0.0)
    pe = p * (1 - k) if pb.get("integrationType") == "Advanced" else p * (1 - m)
    s = np.log(max(60 * (1 / max(1 - pe, 1e-3) - 1), 1e-3))
    keep = m * (1 - k)
    return [np.array([s, s, keep, keep]), np.array([s + 1, s + 0.5, 0.0, 0.0]), np.array([s + 2.5, s + 0.7, 0.3, 0.0]), np.array([s - 1, s, keep, 0.0])]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("set")
    ap.add_argument("--workers", type=int, default=8)
    a = ap.parse_args()
    v = load_set(a.set)
    jobs = [(v["result"], c, v["cases"], starts(c["pb"], c["rig"]["length"])) for c in v["configs"]]
    with ProcessPoolExecutor(a.workers) as ex:
        rows = list(ex.map(percfg, jobs))
    for r in rows:
        pb = r["pb"]
        print(f"{r['name']} p {pb['pull']:.3f} m {pb['spring']:.3f} k {pb.get('stiffness', 0):.3f} seg {r['rig']['segments']} "
              f"err {np.mean(r['err']):6.3f} {np.round(r['err'], 1).tolist()}  S {r['S'][0]:.2f}/{r['S'][1]:.2f} D {r['D'][0]:.3f}/{r['D'][1]:.3f}")
    e = np.array([np.mean(r["err"]) for r in rows])
    print(f"mean {e.mean():.3f} median {np.median(e):.3f} max {e.max():.3f}  cases {v['cases']}")
    json.dump(rows, open(a.set.replace(".json", "_percfg.json"), "w", encoding="utf-8"), indent=1)


if __name__ == "__main__":
    main()
