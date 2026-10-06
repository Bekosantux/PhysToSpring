"""仮説検証: 子の rest 方向を親のシミュ回転 (VRM) と driver の回転 (アニメ姿勢) の間で混ぜる。gamma=1 で VRM と同じ。
joint ごとの S, D は共通 (root/child 2 組) で config ごとに自由最適化し、到達誤差を比べる。"""
import json, sys, numpy as np
sys.path.insert(0, '.')
import fit_global
from numba import njit
from vrm_sim3d import _fromto
from scipy.optimize import minimize

@njit(cache=True)
def sim(drv_pos, drv_rot, root_local, d, L, S, D, gamma, dt):
    T = drv_pos.shape[0]; n = S.shape[0]
    out = np.zeros((n, T, 3)); cur = np.zeros((n, 3))
    h = drv_pos[0] + drv_rot[0] @ root_local; w = drv_rot[0] @ d
    for i in range(n):
        h = h + w * L; cur[i] = h
    prev = cur.copy(); nxt = np.zeros((n, 3))
    for t in range(T):
        R0 = drv_rot[t]; Rp = R0
        h = drv_pos[t] + R0 @ root_local
        for i in range(n):
            ra = R0 @ d; rs = Rp @ d
            rest = gamma * rs + (1 - gamma) * ra
            rest = rest / np.sqrt((rest * rest).sum())
            v = cur[i] + (cur[i] - prev[i]) * (1.0 - D[i]) + rest * S[i] * dt
            e = v - h; nr = np.sqrt((e * e).sum()); e = e / nr if nr > 0 else rest
            nxt[i] = h + e * L
            Rp = _fromto(rs, e) @ Rp
            w = Rp @ d; out[i, t] = w; h = h + w * L
        prev = cur.copy(); cur = nxt.copy()
    return out

data = sys.argv[1]; idx = [int(x) for x in sys.argv[2].split(",")]
fit_global._init(data)
for k in idx:
    cfg, tr = fit_global._DATA[k]
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    def errs(x, gamma):
        S = np.r_[np.exp(x[0]), np.full(n - 1, np.exp(x[1]))] * L
        D = np.clip(np.r_[x[2], np.full(n - 1, x[3])], 0, 1)
        o = []
        for drv, pb in tr:
            v = sim(drv["pos"], drv["rot"], drv["root"], drv["dir"], float(L), S, D, gamma, 1 / 60)
            cc = np.clip(np.einsum("ntk,ntk->nt", v, pb), -1, 1)
            o.append(np.sqrt(np.mean(np.degrees(np.arccos(cc)) ** 2, axis=1)))
        return np.array(o)
    print(cfg["pb"], n, flush=True)
    for gamma in (1.0, 0.5, 0.0):
        best = None
        for x0 in ([np.log(20), np.log(20), 0.5, 0.5], [np.log(5), np.log(5), 0.2, 0.2], [np.log(60), np.log(30), 0.9, 0.9]):
            r = minimize(lambda x: errs(x, gamma).mean(), x0, method="Powell", options={"maxfev": 800})
            if best is None or r.fun < best.fun: best = r
        e = errs(best.x, gamma)
        print(f"  gamma {gamma}: {e.mean():.2f} depth {e.mean(0).round(1)} S {np.exp(best.x[:2]).round(1)} D {best.x[2:].round(3)}", flush=True)
