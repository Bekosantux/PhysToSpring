"""
PhysBone の動きに VRM SpringBone のパラメータを合わせる (CMA-ES)。

1 世代 = 1 ジョブ。各ケースに個体数ぶんの vrmVariants を並べて同時に評価する。
目的関数 = 全ケースの meanErrRmsDeg (セグメント方向差の RMS をセグメント平均したもの) の平均。

使い方: python fit.py fitspecs/base.json [more.json ...] [--out dir]
複数の fitspec (または sweep 付き fitspec) を同じジョブに相乗りさせて並行に最適化する。
"""
import argparse
import copy
import json
import math
import os
import sys
import time
from datetime import datetime

import numpy as np

sys.path.insert(0, os.path.dirname(__file__))
import harness  # noqa: E402

# パラメータ名 -> (無制約空間 -> 実値, 実値 -> 無制約空間)
TRANSFORMS = {
    "stiffness": (np.exp, np.log),
    "gravityPower": (np.exp, np.log),
    "dragForce": (lambda x: 1.0 / (1.0 + np.exp(-x)), lambda y: np.log(y / (1.0 - y))),
}


class Params:
    """joint ごとのパラメータを並べたベクトル。layout = [(param, joint), ...]"""

    def __init__(self, spec):
        self.names = spec["params"]
        self.joints = spec["joints"]
        self.fixed = spec.get("fixed", {})
        self.layout = [(p, j) for p in self.names for j in range(self.joints)]

    def initial(self, init):
        """init[p] はスカラー (全 joint 共通) か joint ごとのリスト。"""
        def value(p, j):
            v = init[p]
            return v[min(j, len(v) - 1)] if isinstance(v, list) else v
        return np.array([TRANSFORMS[p][1](np.float64(value(p, j))) for p, j in self.layout])

    def decode(self, x):
        values = {p: [0.0] * self.joints for p in self.names}
        for (p, j), xi in zip(self.layout, x):
            values[p][j] = float(TRANSFORMS[p][0](xi))
        return values

    def vrm_spec(self, x):
        values = self.decode(x)
        spec = {"stiffness": 1.0, "dragForce": 0.4, "gravityPower": 0.0}
        spec.update(self.fixed)
        spec["perJoint"] = values
        return spec


class CMAES:
    """Hansen の The CMA Evolution Strategy: A Tutorial に沿った最小実装。"""

    def __init__(self, x0, sigma0, popsize=None, seed=0):
        n = len(x0)
        self.n = n
        self.rng = np.random.default_rng(seed)
        self.lam = popsize or 4 + int(3 * math.log(n))
        self.mu = self.lam // 2
        w = math.log(self.mu + 0.5) - np.log(np.arange(1, self.mu + 1))
        self.w = w / w.sum()
        self.mueff = 1.0 / np.sum(self.w ** 2)
        self.cc = (4 + self.mueff / n) / (n + 4 + 2 * self.mueff / n)
        self.cs = (self.mueff + 2) / (n + self.mueff + 5)
        self.c1 = 2 / ((n + 1.3) ** 2 + self.mueff)
        self.cmu = min(1 - self.c1, 2 * (self.mueff - 2 + 1 / self.mueff) / ((n + 2) ** 2 + self.mueff))
        self.damps = 1 + 2 * max(0, math.sqrt((self.mueff - 1) / (n + 1)) - 1) + self.cs
        self.chin = math.sqrt(n) * (1 - 1 / (4 * n) + 1 / (21 * n * n))
        self.m = np.array(x0, dtype=float)
        self.sigma = sigma0
        self.pc = np.zeros(n)
        self.ps = np.zeros(n)
        self.C = np.eye(n)
        self.B = np.eye(n)
        self.D = np.ones(n)
        self.gen = 0

    def ask(self):
        z = self.rng.standard_normal((self.lam, self.n))
        y = z @ np.diag(self.D) @ self.B.T
        return self.m + self.sigma * y

    def tell(self, xs, fs):
        n = self.n
        order = np.argsort(fs)
        xs = xs[order[: self.mu]]
        old = self.m
        self.m = self.w @ xs
        y = (self.m - old) / self.sigma
        c_inv_sqrt = self.B @ np.diag(1 / self.D) @ self.B.T
        self.ps = (1 - self.cs) * self.ps + math.sqrt(self.cs * (2 - self.cs) * self.mueff) * (c_inv_sqrt @ y)
        self.gen += 1
        hsig = np.linalg.norm(self.ps) / math.sqrt(1 - (1 - self.cs) ** (2 * self.gen)) / self.chin < 1.4 + 2 / (n + 1)
        self.pc = (1 - self.cc) * self.pc + hsig * math.sqrt(self.cc * (2 - self.cc) * self.mueff) * y
        ys = (xs - old) / self.sigma
        self.C = ((1 - self.c1 - self.cmu) * self.C
                  + self.c1 * (np.outer(self.pc, self.pc) + (1 - hsig) * self.cc * (2 - self.cc) * self.C)
                  + self.cmu * (ys.T @ np.diag(self.w) @ ys))
        self.sigma *= math.exp((self.cs / self.damps) * (np.linalg.norm(self.ps) / self.chin - 1))
        self.C = np.triu(self.C) + np.triu(self.C, 1).T
        d2, self.B = np.linalg.eigh(self.C)
        self.D = np.sqrt(np.maximum(d2, 1e-20))

    def spread(self):
        """無制約空間での探索幅 (最大軸)。"""
        return self.sigma * float(self.D.max())


def build_cases(spec, vrm_specs, prefix=""):
    cases = []
    for c in spec["cases"]:
        cases.append({
            "name": prefix + c["name"],
            "rig": copy.deepcopy(c.get("rig", spec.get("rig", {}))),
            "pb": copy.deepcopy(c.get("pb", spec["pb"])),
            "motion": c.get("motion", []),
            "warmup": c.get("warmup", 0.2),
            "duration": c.get("duration", 2.0),
            "vrmVariants": vrm_specs,
        })
    return cases


class FitRun:
    """1 つの fitspec (= 1 つの PhysBone 設定) の最適化状態。"""

    def __init__(self, spec, out, resume=None):
        self.spec = spec
        self.name = spec["name"]
        self.out = out
        os.makedirs(out, exist_ok=True)
        with open(os.path.join(out, "fitspec.json"), "w", encoding="utf-8") as fp:
            json.dump(spec, fp, indent=2, ensure_ascii=False)
        self.params = Params(spec)
        init = dict(spec["init"])
        sigma0 = spec.get("sigma0", 0.5)
        if resume:
            # 前回の best から再開する
            for p, values in resume["vrm"]["perJoint"].items():
                if p in init:
                    init[p] = values
            sigma0 = spec.get("resumeSigma", 0.3)
        self.x0 = self.params.initial(init)
        self.sigma0 = sigma0
        self.es = CMAES(self.x0, sigma0, spec.get("popsize"), spec.get("seed", 0))
        self.max_gen = spec.get("maxGen", 150)
        # 停滞判定: tol_window 世代のあいだの改善量が max(tol_f, tol_rel * best) 未満
        self.tol_f = spec.get("tolF", 0.005)
        self.tol_rel = spec.get("tolRel", 0.01)
        self.tol_window = spec.get("tolWindow", 25)
        self.tol_x = spec.get("tolX", 0.01)
        # 停滞したら best から sigma0 で探索し直す回数
        self.restarts = spec.get("restarts", 2)
        self.total_gen = 0
        self.best_f, self.best_x, self.best_cases = math.inf, self.x0, None
        self.history = []
        self.done = None  # 終了理由
        self.xs = None

    def ask(self):
        self.xs = self.es.ask()
        if self.es.gen == 0:
            # 開始点 (初期値 / 再開時の best) も個体 0 として評価する
            self.xs[0] = self.es.m
        return build_cases(self.spec, [self.params.vrm_spec(x) for x in self.xs], self.name + "__")

    def tell(self, result_dir, sec):
        per_case = np.array([[v["meanErrRmsDeg"] for v in harness.load_variants(result_dir, self.name + "__" + c["name"])]
                             for c in self.spec["cases"]])  # [case, variant]
        weights = np.array([c.get("weight", 1.0) for c in self.spec["cases"]])
        f = weights @ per_case / weights.sum()
        self.es.tell(self.xs, f)
        i = int(np.argmin(f))
        if f[i] < self.best_f:
            self.best_f, self.best_x, self.best_cases = float(f[i]), self.xs[i].copy(), per_case[:, i].tolist()
        self.history.append(self.best_f)
        self.total_gen += 1
        rec = {
            "gen": self.total_gen, "best": self.best_f, "genBest": float(f[i]), "genMedian": float(np.median(f)),
            "spread": self.es.spread(), "sec": sec,
            "bestCases": self.best_cases, "bestParams": self.params.decode(self.best_x),
        }
        with open(os.path.join(self.out, "history.jsonl"), "a", encoding="utf-8") as hist:
            hist.write(json.dumps(rec) + "\n")
        with open(os.path.join(self.out, "best.json"), "w", encoding="utf-8") as fp:
            json.dump({"name": self.name, "pb": self.spec["pb"], "err": self.best_f,
                       "cases": dict(zip([c["name"] for c in self.spec["cases"]], self.best_cases)),
                       "vrm": self.params.vrm_spec(self.best_x), "gen": self.total_gen}, fp, indent=2)
        print(f"[{self.name}] gen {self.total_gen:3d}  best {self.best_f:7.3f}  gen-best {f[i]:7.3f}  "
              f"median {np.median(f):7.3f}  spread {self.es.spread():.4f}", flush=True)

        h = self.history
        stalled = len(h) > self.tol_window and h[-self.tol_window - 1] - self.best_f < max(self.tol_f, self.tol_rel * self.best_f)
        shrunk = self.es.spread() < self.tol_x
        if self.total_gen >= self.max_gen:
            self.done = "stopped: maxGen reached"
        elif stalled or shrunk:
            reason = f"stalled over {self.tol_window} generations" if stalled else f"spread < {self.tol_x}"
            if self.restarts > 0:
                self.restarts -= 1
                print(f"[{self.name}] restart from best ({reason})", flush=True)
                self.es = CMAES(self.best_x, self.sigma0, self.spec.get("popsize"), self.spec.get("seed", 0) + 1000 + self.restarts)
                self.history = [self.best_f]
            else:
                self.done = f"converged: {reason}"
        if self.done:
            print(f"[{self.name}] {self.done}  best {self.best_f:.3f}", flush=True)


def load_specs(paths):
    """fitspec を読み込む。"sweep" があれば base を元に 1 パラメータずつ振った spec に展開する。"""
    specs = []
    for path in paths:
        with open(path, encoding="utf-8") as fp:
            spec = json.load(fp)
        sweep = spec.pop("sweep", None)
        if not sweep:
            specs.append(spec)
            continue
        for item in sweep:
            s = copy.deepcopy(spec)
            for key, value in item.items():
                if key == "name":
                    continue
                section, field = key.split(".", 1)  # pb.pull / rig.chains.0.length / init.dragForce
                target = s[section]
                parts = field.split(".")
                for part in parts[:-1]:
                    target = target[int(part)] if isinstance(target, list) else target[part]
                if isinstance(target, list):
                    target[int(parts[-1])] = value
                else:
                    target[parts[-1]] = value
            s["name"] = item.get("name") or spec["name"] + "_" + "_".join(f"{k.split('.')[-1]}{v}" for k, v in item.items())
            specs.append(s)
    return specs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("fitspecs", nargs="+")
    ap.add_argument("--out", default=None, help="出力ディレクトリ (各 spec はその下の {name}/)")
    ap.add_argument("--resume", default=None, help="前回の出力ディレクトリ。{name}/best.json があればそこから再開する")
    args = ap.parse_args()

    specs = load_specs(args.fitspecs)
    out = args.out or os.path.join(harness.JOBS_ROOT, "fits", f"{datetime.now():%Y%m%d_%H%M%S}")
    def resume_of(name):
        path = os.path.join(args.resume, name, "best.json") if args.resume else None
        if path and os.path.exists(path):
            with open(path, encoding="utf-8") as fp:
                return json.load(fp)
        return None

    runs = [FitRun(s, os.path.join(out, s["name"]), resume_of(s["name"])) for s in specs]
    fps = {s.get("fps", 60) for s in specs}
    if len(fps) != 1:
        raise SystemExit("all fitspecs must share fps")
    fps = fps.pop()

    gen = 0
    while True:
        active = [r for r in runs if not r.done]
        if not active:
            break
        cases = []
        for r in active:
            cases += r.ask()
        t0 = time.time()
        job = {"name": "fit", "fps": fps, "rigSpacing": 0.3, "writeTrace": False, "parallelCases": True, "cases": cases}
        result_dir = harness.run(job, f"fit_g{gen:03d}")
        sec = round(time.time() - t0, 1)
        print(f"--- job {gen}: {len(active)} fits, {len(cases)} cases, {sec}s", flush=True)
        for r in active:
            r.tell(result_dir, sec)
        gen += 1

    summary = [{"name": r.name, "pb": r.spec["pb"], "err": r.best_f, "gen": r.total_gen, "done": r.done,
                "cases": dict(zip([c["name"] for c in r.spec["cases"]], r.best_cases)),
                "params": r.params.decode(r.best_x)} for r in runs]
    with open(os.path.join(out, "summary.json"), "w", encoding="utf-8") as fp:
        json.dump(summary, fp, indent=2)
    print(json.dumps({"out": out, "results": [{"name": x["name"], "err": round(x["err"], 3)} for x in summary]}), flush=True)


if __name__ == "__main__":
    main()
