"""
validate.py --trace の結果 (PhysBone のトレース) に対し、VRM の joint ごとの (stiffness, dragForce) を
Python の FastSpringBone 再現 (vrm_sim) で直接最適化する。Unity は使わない。
使い方: python offline_opt.py maps/xxx.json [--workers 6]
"""
import argparse, json, os, sys
from concurrent.futures import ProcessPoolExecutor
import numpy as np
from scipy.optimize import least_squares
sys.path.insert(0, os.path.dirname(__file__))
from vrm_sim import load_driver, load_chain, simulate


def load_cases(rd, cfg, case_names):
    n = cfg["rig"]["segments"]
    out = []
    for c in case_names:
        name = f"{cfg['name']}__{c}"
        dp, dr = load_driver(rd, name)
        out.append((dp, dr, load_chain(rd, name, "pb", n)))
    return out


def errors(data, n, L, S, D):
    """ケースごとの誤差 (セグメント平均の RMS, ハーネスと同じ定義)。"""
    return [float(np.mean(np.sqrt(np.mean((simulate(dp, dr, n, L, S, D) - pb) ** 2, axis=1)))) for dp, dr, pb in data]


def optimize(args):
    rd, cfg, case_names = args[:3]
    mode = args[3] if len(args) > 3 else "joint"
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    data = load_cases(rd, cfg, case_names)
    S0, D0 = cfg["vrm"]["stiffness"], cfg["vrm"]["dragForce"]
    base = errors(data, n, L, S0, D0)
    smax = 60.0 * L / 0.05 * 20
    # joint: 各 joint 独立 / rootchild: 先頭 joint と子 joint (共通) の 2 組
    m = n if mode == "joint" else 2
    expand = (lambda v: v) if mode == "joint" else (lambda v: np.r_[v[0], np.full(n - 1, v[1])])
    def resid(x):
        S, D = expand(x[:m]), expand(x[m:])
        return np.concatenate([(simulate(dp, dr, n, L, S, D) - pb).ravel() / np.sqrt(pb.size) for dp, dr, pb in data])
    x0 = np.r_[np.full(m, max(S0, 1e-3)), np.full(m, min(max(D0, 0.0), 1.0))]
    r = least_squares(resid, x0, bounds=(np.zeros(2 * m), np.r_[np.full(m, smax), np.ones(m)]), diff_step=1e-3, max_nfev=200)
    S, D = expand(r.x[:m]), expand(r.x[m:])
    return {"name": cfg["name"], "pb": cfg["pb"], "rig": cfg["rig"], "formula": {"stiffness": S0, "dragForce": D0},
            "baseErr": base, "opt": {"stiffness": S.tolist(), "dragForce": D.tolist()}, "optErr": errors(data, n, L, S, D),
            "unityErr": cfg.get("cases"), "cases": case_names}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("valjson")
    ap.add_argument("--workers", type=int, default=6)
    ap.add_argument("--mode", default="joint", choices=["joint", "rootchild"])
    ap.add_argument("--out", default=None)
    a = ap.parse_args()
    v = json.load(open(a.valjson, encoding="utf-8"))
    rd = v["result"]
    case_names = list(v["configs"][0]["cases"].keys())
    with ProcessPoolExecutor(a.workers) as ex:
        rows = list(ex.map(optimize, [(rd, c, case_names, a.mode) for c in v["configs"]]))
    for r in rows:
        print(f"{r['name']} p {r['pb']['pull']:.3f} m {r['pb']['spring']:.3f} k {r['pb'].get('stiffness', 0):.3f} seg {r['rig']['segments']} "
              f"unity {np.mean(list(r['unityErr'].values())) if r['unityErr'] else float('nan'):6.3f} sim {np.mean(r['baseErr']):6.3f} -> opt {np.mean(r['optErr']):6.3f}  "
              f"cases {np.round(r['optErr'], 1).tolist()} S {np.round(np.array(r['opt']['stiffness']) / r['rig']['length'] * 0.05, 2).tolist()} D {np.round(r['opt']['dragForce'], 3).tolist()}")
    b = np.array([np.mean(r["baseErr"]) for r in rows]); o = np.array([np.mean(r["optErr"]) for r in rows])
    print(f"formula mean {b.mean():.3f} median {np.median(b):.3f} max {b.max():.3f} | opt mean {o.mean():.3f} median {np.median(o):.3f} max {o.max():.3f}")
    out = a.out or a.valjson.replace(".json", f"_opt_{a.mode}.json")
    json.dump(rows, open(out, "w", encoding="utf-8"), indent=1)


if __name__ == "__main__":
    main()
