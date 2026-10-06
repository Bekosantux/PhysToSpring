"""
変換式の係数を、collect.py のトレース全体に対する平均誤差で直接最適化する (vrm_sim3d, Unity 不要)。
使い方: python fit_global.py MODEL TRAIN.json [--test TEST.json] [--workers 8] [--maxfev 3000]
"""
import argparse, json, os, sys
from multiprocessing import Pool
import numpy as np
from scipy.optimize import minimize
sys.path.insert(0, os.path.dirname(__file__))
from vrm_sim3d import load_trace, simulate, seg_err

DT = 1 / 60
L0 = 0.05
SMAX = 50.0


def adv_base(pb):
    p, m, k = pb["pull"], pb["spring"], pb.get("stiffness", 0.0)
    pe = p * (1 - k)
    s = (1 / max(1 - pe, 1e-3) - 1) / DT
    keep = m * (1 - k) / (1 + 1.374 * k * p / max(1 - pe, 1e-3))
    return s, keep, p, m, k


def model_adv_rc(c, pb, n):
    """先頭/子で別の補正。c: 12 係数。stiffness は L 当たり (L を掛けて使う)。"""
    s, keep, p, m, k = adv_base(pb)
    sr = s * np.exp(c[0] * k + c[1] * k * k + c[2] * k * p)
    sc = s * np.exp(c[3] * k + c[4] * k * k + c[5] * k * p)
    kr = keep * np.exp(-(c[6] * k + c[7] * k * k + c[8] * k * m))
    kc = keep * np.exp(-(c[9] * k + c[10] * k * k + c[11] * k * m))
    S = np.minimum(np.r_[sr, np.full(n - 1, sc)], SMAX / L0)
    K = np.clip(np.r_[kr, np.full(n - 1, kc)], 0, 1)
    return S, 1 - K


def model_adv_rc2(c, pb, n):
    """adv_rc と同じ形だが、keep の補正は減らす方向のみ (keep <= 補正前) にして発散を防ぐ。c: 10 係数。"""
    s, keep, p, m, k = adv_base(pb)
    sr = s * np.exp(c[0] * k + c[1] * k * k + c[2] * k * p)
    sc = s * np.exp(c[3] * k + c[4] * k * k + c[5] * k * p)
    kr = keep * np.exp(-(abs(c[6]) * k + abs(c[7]) * k * k))
    kc = keep * np.exp(-(abs(c[8]) * k + abs(c[9]) * k * k))
    S = np.minimum(np.r_[sr, np.full(n - 1, sc)], SMAX / L0)
    K = np.clip(np.r_[kr, np.full(n - 1, kc)], 0, 1)
    return S, 1 - K


def model_adv_rc3(c, pb, n):
    """adv_rc2 + stiffness 補正に k^2 p, k m 項、keep 補正に k p 項 (すべて keep を下げる方向のみ)。c: 14 係数。"""
    s, keep, p, m, k = adv_base(pb)
    sr = s * np.exp(c[0] * k + c[1] * k * k + c[2] * k * p + c[10] * k * k * p + c[11] * k * m)
    sc = s * np.exp(c[3] * k + c[4] * k * k + c[5] * k * p + c[12] * k * k * p + c[13] * k * m)
    kr = keep * np.exp(-(abs(c[6]) * k + abs(c[7]) * k * k))
    kc = keep * np.exp(-(abs(c[8]) * k * p + abs(c[9]) * k * k))
    S = np.minimum(np.r_[sr, np.full(n - 1, sc)], SMAX / L0)
    K = np.clip(np.r_[kr, np.full(n - 1, kc)], 0, 1)
    return S, 1 - K


def model_simp_rc(c, pb, n):
    """
    Simplified: 閉形式 + lerp_v3 と同じ形の高 spring 補正。先頭/子で補正の強さ (a0, b0) を分ける。
    c: [a0_root, a1, a2, b0_root, b1, b2, b3, a0_child, b0_child]。全部 0 なら閉形式そのもの。
    """
    p, q = pb["pull"], pb["spring"]
    r = 1 - p * (1 - q)
    s = (1 / max(r, 1e-3) - 1) / DT
    keep = q / max(r, 1e-3)
    a1, a2, b1, b2, b3 = abs(c[1]) + 1e-3, c[2], abs(c[4]), c[5], abs(c[6]) + 1e-3
    def corr(a0, b0):
        sf = 1 + a0 * max(0.0, q - a2) ** 2 / (p + a1)
        kf = max(0.0, 1 - b0 * max(0.0, q - b2) ** b3 / (1 + b1 * p))
        return min(s * sf, SMAX / L0), min(keep * kf, 1.0)
    sr, kr = corr(c[0], c[3])
    sc, kc = corr(c[7], c[8])
    S = np.r_[sr, np.full(n - 1, sc)]
    K = np.r_[kr, np.full(n - 1, kc)]
    return S, 1 - K


def depth_terms(n):
    """子 joint (i >= 1) の深さ特徴量。t = log(i) (i=1 で 0)、r = i / (n-1) (チェーン内の相対位置)。"""
    i = np.arange(1, n, dtype=float)
    return np.log(i), i / max(n - 1, 1)


def model_adv_rc4(c, pb, n):
    """
    adv_rc3 + 子 joint の深さ補正。c: 14 + 7 係数。
    stiffness *= exp(t (d0 + d1 k + d2 p) + r (d3 + d4 k))、keep *= exp(-(d5 t + d6 r))。
    """
    S, D = model_adv_rc3(c[:14], pb, n)
    if n <= 1:
        return S, D
    d = c[14:]
    p, k = pb["pull"], pb.get("stiffness", 0.0)
    t, r = depth_terms(n)
    S = S.copy()
    K = 1 - D
    S[1:] = np.minimum(S[1:] * np.exp(t * (d[0] + d[1] * k + d[2] * p) + r * (d[3] + d[4] * k)), SMAX / L0)
    K[1:] = np.clip(K[1:] * np.exp(-(d[5] * t + d[6] * r)), 0, 1)
    return S, 1 - K


def model_simp_rc2(c, pb, n):
    """
    simp_rc + 子 joint の深さ補正。c: 9 + 7 係数。
    stiffness *= exp(t (d0 + d1 q + d2 p) + r (d3 + d4 q))、keep *= exp(-(d5 t + d6 r))。
    """
    S, D = model_simp_rc(c[:9], pb, n)
    if n <= 1:
        return S, D
    d = c[9:]
    p, q = pb["pull"], pb["spring"]
    t, r = depth_terms(n)
    S = S.copy()
    K = 1 - D
    S[1:] = np.minimum(S[1:] * np.exp(t * (d[0] + d[1] * q + d[2] * p) + r * (d[3] + d[4] * q)), SMAX / L0)
    K[1:] = np.clip(K[1:] * np.exp(-(d[5] * t + d[6] * r)), 0, 1)
    return S, 1 - K


MODELS = {"adv_rc": (model_adv_rc, 12), "adv_rc2": (model_adv_rc2, 10), "adv_rc3": (model_adv_rc3, 14),
          "simp_rc": (model_simp_rc, 9), "adv_rc4": (model_adv_rc4, 21), "simp_rc2": (model_simp_rc2, 16)}

_DATA = None
_AMP = 0.0


def amp(x):
    """各セグメントのワールド方向の、最初のフレーム (静止) からの角度の RMS (deg)。振れ幅の目安。"""
    c = np.clip(np.einsum("ntk,nk->nt", x, x[:, 0]), -1, 1)
    return np.sqrt(np.mean(np.degrees(np.arccos(c)) ** 2, axis=1))


def case_err(sim, pb):
    """軌跡の誤差 + _AMP x 振れ幅の差 (joint 平均)。"""
    e = seg_err(sim, pb)
    if _AMP > 0:
        e += _AMP * float(np.mean(np.abs(amp(sim) - amp(pb))))
    return e


def _init(paths, amp_w=0.0):
    global _DATA, _AMP
    _AMP = amp_w
    _DATA = []
    for path in paths.split(","):
        v = json.load(open(path, encoding="utf-8"))
        for cfg in v["configs"]:
            n = cfg["rig"]["segments"]
            _DATA.append((cfg, [load_trace(v["result"], f"{cfg['name']}__{cn}", n)[:2] for cn in v["cases"]]))


def _eval(args):
    model, c, i = args
    cfg, data = _DATA[i]
    n, L = cfg["rig"]["segments"], cfg["rig"]["length"]
    S, D = MODELS[model][0](c, cfg["pb"], n)
    return [case_err(simulate(drv, n, L, S * L, D), pb) for drv, pb in data]


class Objective:
    def __init__(self, path, workers, amp_w=0.0):
        self.path = path
        self.n = sum(len(json.load(open(p, encoding="utf-8"))["configs"]) for p in path.split(","))
        self.pool = Pool(workers, initializer=_init, initargs=(path, amp_w))

    def errors(self, model, c):
        return np.array(self.pool.map(_eval, [(model, np.asarray(c), i) for i in range(self.n)]))

    def __call__(self, model, c):
        return float(self.errors(model, c).mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model")
    ap.add_argument("train")
    ap.add_argument("--test", default=None)
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--maxfev", type=int, default=3000)
    ap.add_argument("--x0", default=None, help="係数の初期値 (json ファイル)")
    ap.add_argument("--x0init", default=None, help="係数の初期値 (json 配列)")
    ap.add_argument("--amp", type=float, default=0.0, help="目的関数に足す振れ幅の差の重み")
    ap.add_argument("--tag", default="", help="出力ファイル名に付ける")
    ap.add_argument("--fix", type=int, default=0, help="先頭 N 個の係数を固定して残りだけ最適化する")
    a = ap.parse_args()
    fn, npar = MODELS[a.model]
    x0 = np.zeros(npar)
    if a.x0:
        c0 = np.array(json.load(open(a.x0))["coef"])[:npar]
        x0[:len(c0)] = c0
    if a.x0init:
        x0 = np.array(json.loads(a.x0init))
    tr = Objective(a.train, a.workers, a.amp)
    e0 = tr.errors(a.model, x0)
    print("train start", e0.mean().round(3), "per case", e0.mean(0).round(2), flush=True)
    it = [0]
    full = lambda c: np.r_[x0[:a.fix], c]
    def f(c):
        c = full(c)
        v = tr(a.model, c)
        it[0] += 1
        if it[0] % 100 == 0:
            print(it[0], round(v, 4), np.round(c, 3).tolist(), flush=True)
        return v
    r = minimize(f, x0[a.fix:], method="Powell", options={"maxfev": a.maxfev, "xtol": 1e-3, "ftol": 1e-5})
    r.x = full(r.x)
    e = tr.errors(a.model, r.x)
    print("train end", e.mean().round(3), "median", np.median(e.mean(1)).round(3), "per case", e.mean(0).round(2))
    out = {"model": a.model, "coef": r.x.tolist(), "train": float(e.mean())}
    if a.test:
        te = Objective(a.test, a.workers, a.amp)
        e1, e2 = te.errors(a.model, x0), te.errors(a.model, r.x)
        print("test start", e1.mean().round(3), "end", e2.mean().round(3), "median", np.median(e2.mean(1)).round(3), "per case", e2.mean(0).round(2))
        out["test"] = float(e2.mean())
    path = os.path.join(os.path.dirname(a.train.split(",")[0]), f"coef_{a.model}{a.tag}.json")
    json.dump(out, open(path, "w"), indent=1)
    print(path, np.round(r.x, 4).tolist())


if __name__ == "__main__":
    main()
