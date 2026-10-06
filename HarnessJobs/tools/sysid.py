"""
PhysBone の自由振動から 1 フレーム更新の線形漸化式 (AR(2) 程度) を同定する。
小さく回して止めた後の減衰を使い、θ_{n+1} = a1 θ_n + a2 θ_{n-1} を最小二乗で求める。
VRM の joint 単体の線形化 θ' = [θ + (1-D)(θ-θprev)] / (1+k) と比べると
  a1 = (2-D)/(1+k),  a2 = -(1-D)/(1+k)  →  1+k = -(1-D)/a2 ...
"""
import csv, os, sys
import numpy as np
from scipy.spatial.transform import Rotation as R
sys.path.insert(0, os.path.dirname(__file__))
import harness

WARM, MOVE = 0.5, 0.1


def make_case(name, pb, amp=6.0):
    return {"name": name, "rig": {"chains": [{"segments": 1, "length": 0.05, "direction": [0, -1, 0]}]}, "pb": pb,
            "motion": [{"type": "rampRot", "axis": [0, 0, 1], "amplitude": amp, "length": MOVE}],
            "warmup": WARM, "duration": 3.0,
            "vrm": {"stiffness": 1.0, "dragForce": 0.5, "gravityPower": 0.0, "perJoint": {}}}


def angles(rd, case, fps=60):
    rows = [r for r in csv.DictReader(open(os.path.join(rd, case, "trace.csv"))) if r["rig"] == "pb" and r["bone"] == "C0_0"]
    q = lambda r: R.from_quat([float(r[k]) for k in ("qx", "qy", "qz", "qw")])
    q0 = q(rows[0])
    out = []
    for r in rows:
        d = (q(r) * q0.inv()).apply([0, -1, 0])
        out.append(np.degrees(np.arctan2(d[0], -d[1])))
    return np.array(out)


def fit_ar(theta, target, fps=60, order=2):
    """motion 終了後の偏差 e で AR を当てる。1 次で十分なら 1 次 (2 次は列が従属して不定になる)。"""
    start = int((WARM + MOVE) * fps) + 3
    e = theta[start:] - target
    big = np.nonzero(np.abs(e) > 1e-3 * np.abs(e).max())[0]
    e = e[: max(int(big.max()) + 1 if len(big) else 0, 8)]
    best = None
    for o in (1, 2):
        X = np.column_stack([e[o - 1 - i: len(e) - 1 - i] for i in range(o)])
        y = e[o:]
        a, *_ = np.linalg.lstsq(X, y, rcond=None)
        resid = float(np.sqrt(np.mean((X @ a - y) ** 2)) / np.abs(e).max())
        best = (a, resid)
        if resid < 1e-3:
            break
    return best[0], best[1], e


def poles(a):
    return np.roots([1, *(-np.asarray(a))])


def vrm_from_poles(pl, length=0.05, dt=1 / 60):
    """VRM の特性多項式 z^2 - (1+m)/(1+k) z + m/(1+k): 和 - 積 = 1/(1+k), 積 = m/(1+k)。"""
    pl = list(pl) + [0.0] * (2 - len(pl))
    sm, pr = float(np.real(pl[0] + pl[1])), float(np.real(pl[0] * pl[1]))
    one_plus_k = 1.0 / max(sm - pr, 1e-6)
    m = pr * one_plus_k
    return {"stiffness": (one_plus_k - 1) * length / dt, "dragForce": 1 - m}


if __name__ == "__main__":
    import json
    sys.path.insert(0, os.path.dirname(__file__))
    from mapping import lerp_model
    cfgs = [(0.2, 0.0), (0.2, 0.4), (0.5, 0.6), (0.1, 0.8)]
    cases = [make_case(f"p{p}_s{s}", {"integrationType": "Simplified", "pull": p, "spring": s, "stiffness": 0.2, "gravity": 0}) for p, s in cfgs]
    rd = harness.run({"name": "sysid", "fps": 60, "rigSpacing": 0.3, "writeTrace": True, "parallelCases": True, "cases": cases}, "sysid")
    for c, (p, s) in zip(cases, cfgs):
        th = angles(rd, c["name"])
        a, res, e = fit_ar(th, th[-1])
        print(c["name"], "AR", a.round(4), "resid", round(res, 5), "-> vrm", {k: round(v, 3) for k, v in vrm_from_poles(poles(a)).items()},
              "lerp", {k: round(v, 3) for k, v in lerp_model(c["pb"], 0.05).items() if k != "gravityPower"},
              "poles", poles(a).round(3), "lerp poles", np.roots([1, -(1 + s - p * (1 - s)), s]).round(3))
