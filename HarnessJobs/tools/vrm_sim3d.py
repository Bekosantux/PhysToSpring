"""
UniVRM FastSpringBone (UpdateFastSpringBoneJob) の 3D 再現 (numba)。直線チェーン、コライダー/リミットなし。
トレースの位置は driver ローカルなので、driver の回転でワールドに直して比べる。
"""
import csv, os
import numpy as np
from numba import njit
from scipy.spatial.transform import Rotation as R

EPS = 1e-6


@njit(cache=True)
def _fromto(a, b):
    """MathHelper.FromToRotation の回転行列 (a, b は単位ベクトル)。"""
    dot = min(max(a[0] * b[0] + a[1] * b[1] + a[2] * b[2], -1.0), 1.0)
    I = np.eye(3)
    if dot > 1.0 - EPS:
        return I
    ax = np.array([a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]])
    n = np.sqrt((ax * ax).sum())
    if n < 1e-12:
        ax = np.array([0.0, -a[2], a[1]])  # cross(a, (1,0,0))
        n = np.sqrt((ax * ax).sum())
    ax = ax / n
    ang = np.arccos(dot)
    K = np.array([[0.0, -ax[2], ax[1]], [ax[2], 0.0, -ax[0]], [-ax[1], ax[0], 0.0]])
    return I + np.sin(ang) * K + (1 - np.cos(ang)) * (K @ K)


@njit(cache=True)
def _simulate(drv_pos, drv_rot, root_local, d, L, S, D, G, gdir, dt):
    T = drv_pos.shape[0]
    n = S.shape[0]
    out = np.zeros((n, T, 3))
    cur = np.zeros((n, 3))
    h = drv_pos[0] + drv_rot[0] @ root_local
    w = drv_rot[0] @ d
    for i in range(n):
        h = h + w * L
        cur[i] = h
    prev = cur.copy()
    nxt = np.zeros((n, 3))
    for t in range(T):
        Rp = drv_rot[t]
        h = drv_pos[t] + Rp @ root_local
        for i in range(n):
            rest = Rp @ d
            v = cur[i] + (cur[i] - prev[i]) * (1.0 - D[i]) + rest * S[i] * dt + gdir * G[i] * dt
            e = v - h
            nr = np.sqrt((e * e).sum())
            e = e / nr if nr > 0 else rest
            nxt[i] = h + e * L
            Rp = _fromto(rest, e) @ Rp
            w = Rp @ d
            out[i, t] = w
            h = h + w * L
        prev = cur.copy()
        cur = nxt.copy()
    return out


def simulate(drv, n, L, stiff, drag, grav=0.0, gdir=(0.0, -1.0, 0.0), dt=1 / 60):
    """drv = load_driver の戻り値。戻り値: 各 joint のワールド方向 (n, T, 3)。"""
    S = np.ascontiguousarray(np.broadcast_to(np.asarray(stiff, float), (n,)))
    D = np.ascontiguousarray(np.broadcast_to(np.asarray(drag, float), (n,)))
    G = np.ascontiguousarray(np.broadcast_to(np.asarray(grav, float), (n,)))
    return _simulate(drv["pos"], drv["rot"], drv["root"], drv["dir"], float(L), S, D, G, np.asarray(gdir, float), dt)


def load_trace(rd, case, nseg):
    """driver (位置・回転・Root のローカル位置・チェーン方向) と PhysBone のワールド方向 (n, T, 3) を返す。"""
    names = ["Root"] + [f"C0_{i}" for i in range(nseg)]
    dpos, dq, P = [], [], {(rig, nm): [] for rig in ("pb", "vrm") for nm in names}
    for r in csv.DictReader(open(os.path.join(rd, case, "trace.csv"))):
        if r["rig"] == "driver":
            dpos.append([float(r["px"]), float(r["py"]), float(r["pz"])])
            dq.append([float(r[k]) for k in ("qx", "qy", "qz", "qw")])
        elif (r["rig"], r["bone"]) in P:
            P[(r["rig"], r["bone"])].append([float(r["px"]), float(r["py"]), float(r["pz"])])
    rot = R.from_quat(dq).as_matrix()
    P = {k: np.array(v) for k, v in P.items()}
    def dirs(rig):
        out = []
        for i in range(nseg):
            loc = P[(rig, names[i + 1])] - P[(rig, names[i])]
            loc = loc / np.linalg.norm(loc, axis=1, keepdims=True)
            out.append(np.einsum("tij,tj->ti", rot, loc))
        return np.array(out)
    d0 = P[("pb", names[1])][0] - P[("pb", names[0])][0]
    drv = {"pos": np.array(dpos), "rot": np.ascontiguousarray(rot), "root": P[("pb", "Root")][0].copy(), "dir": d0 / np.linalg.norm(d0)}
    return drv, dirs("pb"), dirs("vrm")


def seg_err(a, b):
    """ハーネスと同じ: joint ごとに角度誤差の RMS (deg) を取り平均。"""
    c = np.clip(np.einsum("ntk,ntk->nt", a, b), -1, 1)
    return float(np.mean(np.sqrt(np.mean(np.degrees(np.arccos(c)) ** 2, axis=1))))
