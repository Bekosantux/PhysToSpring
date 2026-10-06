"""
PhysBone の 1 分割トレース (ワールド角) に、VRM joint の線形化モデルを当てて (k, m) を直接求める。
  φ' = [φ + m (φ - φprev) + k ρ'] / (1 + k)     ρ = 親 (ルート) の角度, k = S dt / L, m = 1 - drag
"""
import csv, os, sys
import numpy as np
from scipy.optimize import least_squares
from scipy.spatial.transform import Rotation as R


def series(rd, case, rig, bone, axis=(0, -1, 0)):
    rows = [r for r in csv.DictReader(open(os.path.join(rd, case, "trace.csv"))) if r["rig"] == rig and r["bone"] == bone]
    q = lambda r: R.from_quat([float(r[k]) for k in ("qx", "qy", "qz", "qw")])
    q0 = q(rows[0])
    out = []
    for r in rows:
        d = (q(r) * q0.inv()).apply(axis)
        out.append(np.degrees(np.arctan2(d[0], -d[1])))
    return np.array(out)


def simulate(k, m, rho):
    phi = np.zeros_like(rho)
    phi[0] = phi[1] = rho[0]
    for n in range(1, len(rho) - 1):
        phi[n + 1] = (phi[n] + m * (phi[n] - phi[n - 1]) + k * rho[n + 1]) / (1 + k)
    return phi


def fit(phi_pb, rho, x0=(0.3, 0.3), lag=0):
    rho_in = np.concatenate([np.full(lag, rho[0]), rho[: len(rho) - lag]]) if lag else rho
    f = lambda x: simulate(x[0], x[1], rho_in) - phi_pb
    r = least_squares(f, x0, bounds=([0, 0], [100, 1]))
    return r.x, float(np.sqrt(np.mean(r.fun ** 2)))


if __name__ == "__main__":
    import glob
    rd = sorted(glob.glob(os.path.join(os.path.dirname(__file__), "..", "results", "*_sysid")))[-1]
    for c in sorted(os.listdir(rd)):
        if not os.path.isdir(os.path.join(rd, c)):
            continue
        drv = series(rd, c, "driver", "Driver")
        pb = series(rd, c, "pb", "C0_0") + drv  # トレースの回転はローカルなのでワールド角に直す
        print(c, "driver end", round(drv[-1], 2), "pb end", round(pb[-1], 2))
        for lag in (0, 1):
            (k, m), rms = fit(pb, drv, lag=lag)
            print("   lag", lag, "k", round(k, 4), "m", round(m, 4), "-> S", round(k * 0.05 * 60, 3), "drag", round(1 - m, 3), "rms", round(rms, 4))
