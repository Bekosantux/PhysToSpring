"""
UniVRM FastSpringBone (UpdateFastSpringBoneJob) の 2D (xy 平面) 再現。直線チェーン、コライダー/リミットなし。
角度の向き: dir(a) = (sin a, -cos a)  (a = 0 で真下)。
"""
import csv, os
import numpy as np


def dirv(a):
    return np.stack([np.sin(a), -np.cos(a)], axis=-1)


def ang(v):
    return np.arctan2(v[..., 0], -v[..., 1])


def load_driver(rd, case):
    """driver の (位置 xy, z 軸回りの角度 [rad]) をフレーム順に。"""
    pos, rot = [], []
    for r in csv.DictReader(open(os.path.join(rd, case, "trace.csv"))):
        if r["rig"] == "driver":
            pos.append([float(r["px"]), float(r["py"])])
            rot.append(2 * np.arctan2(float(r["qz"]), float(r["qw"])))
    return np.array(pos), np.array(rot)


def load_chain(rd, case, rig, nseg):
    """各ボーンのワールド角 [deg] (位置から)。shape (nseg, T)。トレースの位置は driver ローカルなので driver 角を足す。"""
    names = ["Root"] + [f"C0_{i}" for i in range(nseg)]
    P = {n: [] for n in names}
    for r in csv.DictReader(open(os.path.join(rd, case, "trace.csv"))):
        if r["rig"] == rig and r["bone"] in P:
            P[r["bone"]].append([float(r["px"]), float(r["py"])])
    P = {n: np.array(v) for n, v in P.items()}
    _, rot = load_driver(rd, case)
    return np.degrees(np.array([ang(P[names[i + 1]] - P[names[i]]) + rot for i in range(nseg)]))


def simulate(drv_pos, drv_rot, nseg, L, stiff, drag, grav=None, gdir=(0.0, -1.0), dt=1 / 60):
    """stiff/drag/grav はスカラーか joint ごとの配列。戻り値: ワールド角 [deg] shape (nseg, T)。"""
    S = np.broadcast_to(np.asarray(stiff, float), (nseg,))
    D = np.broadcast_to(np.asarray(drag, float), (nseg,))
    G = np.broadcast_to(np.asarray(0.0 if grav is None else grav, float), (nseg,))
    gdir = np.asarray(gdir, float)
    T = len(drv_rot)
    # 初期: rest (driver の初期姿勢でまっすぐ)
    a = np.full(nseg, drv_rot[0])
    head = drv_pos[0] + np.concatenate([[[0, 0]], np.cumsum(L * dirv(a[:-1]), axis=0)]) if nseg > 1 else drv_pos[0][None]
    cur = head + L * dirv(a)
    prev = cur.copy()
    out = np.zeros((nseg, T))
    for t in range(T):
        parent_a, h = drv_rot[t], drv_pos[t]
        nxt = np.zeros_like(cur)
        for i in range(nseg):
            n = cur[i] + (cur[i] - prev[i]) * (1 - D[i]) + dirv(parent_a) * S[i] * dt + gdir * G[i] * dt
            d = n - h
            nrm = np.linalg.norm(d)
            n = h + (d / nrm if nrm > 0 else dirv(parent_a)) * L
            nxt[i] = n
            # MathHelper.FromToRotation: rest との角度が acos(1-1e-6) 未満なら回転は rest に丸める (tail 状態はそのまま)
            a_new = ang(n - h)
            if np.cos(a_new - parent_a) > 1 - 1e-6:
                a_new = parent_a
            parent_a = a_new
            out[i, t] = parent_a
            h = h + L * dirv(parent_a)
        prev, cur = cur, nxt
    return np.degrees(out)
