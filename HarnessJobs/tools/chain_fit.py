"""
多分割チェーンの PhysBone (ワールド角) に VRM チェーンの線形化モデルを当てる。
  joint i: φ_i' = [φ_i + m_i (φ_i - φ_i,prev) + k_i φ_{i-1}'] / (1 + k_i)    φ_{-1} = driver
"""
import csv, os, sys, json
import numpy as np
from scipy.optimize import least_squares
from scipy.spatial.transform import Rotation as R
sys.path.insert(0, os.path.dirname(__file__))
import harness

WARM, MOVE = 0.5, 0.1


def local_angles(rd, case, rig, bone, axis=(0, -1, 0)):
    rows = [r for r in csv.DictReader(open(os.path.join(rd, case, "trace.csv"))) if r["rig"] == rig and r["bone"] == bone]
    q = lambda r: R.from_quat([float(r[k]) for k in ("qx", "qy", "qz", "qw")])
    q0 = q(rows[0])
    out = []
    for r in rows:
        d = (q(r) * q0.inv()).apply(axis)
        out.append(np.degrees(np.arctan2(d[0], -d[1])))
    return np.array(out)


def world_angles(rd, case, nseg):
    drv = local_angles(rd, case, "driver", "Driver")
    acc, out = drv.copy(), []
    for i in range(nseg):
        acc = acc + local_angles(rd, case, "pb", "Root" if i == 0 else f"C0_{i - 1}")
        out.append(acc.copy())
    return drv, np.array(out)


def simulate_chain(ks, ms, rho):
    n, T = len(ks), len(rho)
    phi = np.zeros((n, T))
    phi[:, 0] = phi[:, 1] = rho[0]
    for t in range(1, T - 1):
        parent = rho[t + 1]
        for i in range(n):
            phi[i, t + 1] = (phi[i, t] + ms[i] * (phi[i, t] - phi[i, t - 1]) + ks[i] * parent) / (1 + ks[i])
            parent = phi[i, t + 1]
    return phi


def fit_chain(phi_pb, rho, shared=False):
    n = phi_pb.shape[0]
    def unpack(x):
        return (np.full(n, x[0]), np.full(n, x[1])) if shared else (x[:n], x[n:])
    f = lambda x: (simulate_chain(*unpack(x), rho) - phi_pb).ravel()
    x0 = [0.3, 0.3] if shared else [0.3] * n + [0.3] * n
    r = least_squares(f, x0, bounds=(0, [100] * (len(x0) // 2) + [1] * (len(x0) // 2)))
    k, m = unpack(r.x)
    return k, m, float(np.sqrt(np.mean(r.fun ** 2)))


if __name__ == "__main__":
    from mapping import advanced_model
    nseg = 3
    cfgs = [(0.4, 0.0, 0.0), (0.4, 0.0, 0.5), (0.4, 0.6, 0.5), (0.2, 0.3, 0.8)]
    cases = []
    for p, mo, st in cfgs:
        pb = {"integrationType": "Advanced", "pull": p, "spring": mo, "stiffness": st, "gravity": 0}
        cases.append({"name": f"p{p}_m{mo}_k{st}", "pb": pb,
                      "rig": {"chains": [{"segments": nseg, "length": 0.05, "direction": [0, -1, 0]}]},
                      "motion": [{"type": "rampRot", "axis": [0, 0, 1], "amplitude": 6.0, "length": MOVE}],
                      "warmup": WARM, "duration": 3.0,
                      "vrm": {"stiffness": 1.0, "dragForce": 0.5, "gravityPower": 0.0, "perJoint": {}}})
    rd = harness.run({"name": "chainfit", "fps": 60, "rigSpacing": 0.3, "writeTrace": True, "parallelCases": True, "cases": cases}, "chainfit")
    print(rd)
    for c in cases:
        rho, phi = world_angles(rd, c["name"], nseg)
        mp = advanced_model(c["pb"], c["pb"]["pull"], c["pb"]["spring"], 0.05, 1 / 60, np.pi / 2, 0.0)
        kf, mf = mp["stiffness"] / 60 / 0.05, 1 - mp["dragForce"]
        pred = simulate_chain([kf] * nseg, [mf] * nseg, rho)
        print(c["name"], "formula k %.3f m %.3f rms %.3f" % (kf, mf, np.sqrt(np.mean((pred - phi) ** 2))))
        k, m, rms = fit_chain(phi, rho)
        print("   per-joint fit k", k.round(3), "m", m.round(3), "rms %.4f" % rms)
        for i in range(nseg):
            print("   joint", i, "peak pb %.2f vrm %.2f  end pb %.2f" % (phi[i].max(), pred[i].max(), phi[i][-1]))
