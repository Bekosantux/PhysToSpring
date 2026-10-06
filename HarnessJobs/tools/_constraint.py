"""仮説検証: 見えない補助チェーンを SpringBone で揺らし、表示ボーンへ VRMC_node_constraint (rotation) で
重み c_i (0..1) を掛けてコピーする。constraint は SpringBone より前に評価されるので 1 フレーム遅れ (lag=1)。
c_i を深さごとに自由最適化 (rc3 の S, D のまま / S, D も自由) し、現行 (c=1, 遅れなし) と比べる。
使い方: python _constraint.py MODEL COEF.json DATA.json IDX[,IDX..] [--free] [--lag 1]"""
import argparse, json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from numba import njit
from vrm_sim3d import _fromto
from scipy.optimize import minimize


@njit(cache=True)
def _rod(ax, ang):
    K = np.array([[0.0, -ax[2], ax[1]], [ax[2], 0.0, -ax[0]], [-ax[1], ax[0], 0.0]])
    return np.eye(3) + np.sin(ang) * K + (1 - np.cos(ang)) * (K @ K)


@njit(cache=True)
def sim(drv_pos, drv_rot, root_local, d, L, S, D, C, lag, dt):
    """補助チェーン = vrm_sim3d と同じ。各 joint の局所回転 (親基準の軸と角度) を記録し、
    表示チェーンは drv_rot から局所回転を c 倍 (slerp(I, delta, c)) して積み直す。"""
    T = drv_pos.shape[0]; n = S.shape[0]
    axl = np.zeros((n, T, 3)); ang = np.zeros((n, T))
    cur = np.zeros((n, 3))
    h = drv_pos[0] + drv_rot[0] @ root_local; w = drv_rot[0] @ d
    for i in range(n):
        h = h + w * L; cur[i] = h
    prev = cur.copy(); nxt = np.zeros((n, 3))
    for t in range(T):
        Rp = drv_rot[t]
        h = drv_pos[t] + Rp @ root_local
        for i in range(n):
            rest = Rp @ d
            v = cur[i] + (cur[i] - prev[i]) * (1.0 - D[i]) + rest * S[i] * dt
            e = v - h; nr = np.sqrt((e * e).sum()); e = e / nr if nr > 0 else rest
            nxt[i] = h + e * L
            A = _fromto(rest, e)
            dot = min(max((rest * e).sum(), -1.0), 1.0)
            if A[0, 0] != 1.0 or A[1, 1] != 1.0 or A[2, 2] != 1.0:
                ax = np.array([rest[1] * e[2] - rest[2] * e[1], rest[2] * e[0] - rest[0] * e[2], rest[0] * e[1] - rest[1] * e[0]])
                ax = ax / np.sqrt((ax * ax).sum())
                axl[i, t] = Rp.T @ ax; ang[i, t] = np.arccos(dot)
            Rp = A @ Rp
            w = Rp @ d; h = h + w * L
        prev = cur.copy(); cur = nxt.copy()
    out = np.zeros((n, T, 3))
    for t in range(T):
        V = drv_rot[t].copy(); s = t - lag
        for i in range(n):
            if s >= 0 and ang[i, s] > 0:
                V = V @ _rod(axl[i, s], C[i] * ang[i, s])
            out[i, t] = V @ d
    return out


def errs(cfg, tr, S, D, C, lag):
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    o = []
    for drv, pb in tr:
        v = sim(drv["pos"], drv["rot"], drv["root"], drv["dir"], float(L), S * L, D, C, lag, 1 / 60)
        cc = np.clip(np.einsum("ntk,ntk->nt", v, pb), -1, 1)
        o.append(np.sqrt(np.mean(np.degrees(np.arccos(cc)) ** 2, axis=1)))
    return np.array(o)  # cases x n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model"); ap.add_argument("coef"); ap.add_argument("data"); ap.add_argument("idx")
    ap.add_argument("--free", action="store_true"); ap.add_argument("--lag", type=int, default=1)
    a = ap.parse_args()
    c = np.array(json.load(open(a.coef))["coef"])
    fit_global._init(a.data)
    idx = range(len(fit_global._DATA)) if a.idx == "all" else [int(x) for x in a.idx.split(",")]
    for k in idx:
        cfg, tr = fit_global._DATA[k]
        n = cfg["rig"]["segments"]
        S0, D0 = fit_global.MODELS[a.model][0](c, cfg["pb"], n)
        e0 = errs(cfg, tr, S0, D0, np.ones(n), 0)
        e1l = errs(cfg, tr, S0, D0, np.ones(n), a.lag)
        sig = lambda x: 1 / (1 + np.exp(-x))
        if a.free:
            def unpack(x): return np.exp(x[:n]), np.clip(np.exp(x[n:2 * n]), 0, 1), sig(x[2 * n:])
            x0 = np.r_[np.log(S0), np.log(np.clip(D0, 1e-4, 1)), np.full(n, 4.0)]
        else:
            def unpack(x): return S0, D0, sig(x)
            x0 = np.full(n, 4.0)
        r = minimize(lambda x: errs(cfg, tr, *unpack(x), a.lag).mean(), x0, method="Powell",
                     options={"maxfev": 6000 if a.free else 2000, "xtol": 1e-3, "ftol": 1e-5})
        S, D, C = unpack(r.x)
        e2 = errs(cfg, tr, S, D, C, a.lag)
        pb = cfg["pb"]
        print(f"[{k}] pull {pb['pull']} spring {pb['spring']} k {pb.get('stiffness')} n {n}", flush=True)
        print(f"  current      {e0.mean():6.2f}  depth {e0.mean(0).round(1)}")
        print(f"  c=1 lag{a.lag}     {e1l.mean():6.2f}")
        print(f"  constraint   {e2.mean():6.2f}  depth {e2.mean(0).round(1)}  c {C.round(2)}", flush=True)


if __name__ == "__main__":
    main()
