"""
オフライン最適化用に PhysBone のトレースを集める (VRM 側の値は何でもよい)。
ケース: sweep (並進はボーン長 / 0.05 倍) + realistic (そのまま)。
使い方: python collect.py NAME --integration Advanced --n 60 --seed 11 [--spring-max 0.9] [--gravity-max 0] [--falloff-max 0]
"""
import argparse, copy, json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
import harness
from validate import random_configs, BASE_SPEC, GRAVITY_SPEC

REALISTIC = os.path.join(harness.JOBS_ROOT, "fitspecs", "realistic_cases.json")


def scaled(motion, k):
    motion = copy.deepcopy(motion)
    for mo in motion:
        if mo["type"] in ("step", "sinePos", "chirpPos", "rampPos"):
            mo["amplitude"] = mo.get("amplitude", 0.1) * k
            mo["position"] = [x * k for x in mo.get("position", [0, 0, 0])]
    return motion


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("name")
    ap.add_argument("--integration", default="Simplified")
    ap.add_argument("--n", type=int, default=60)
    ap.add_argument("--seed", type=int, default=11)
    ap.add_argument("--spring-max", type=float, default=0.9)
    ap.add_argument("--gravity-max", type=float, default=0.0)
    ap.add_argument("--falloff-max", type=float, default=0.0)
    ap.add_argument("--no-realistic", action="store_true")
    ap.add_argument("--pull", default="0.05,1.0", help="pull の範囲 MIN,MAX")
    ap.add_argument("--spring-min", type=float, default=0.0)
    ap.add_argument("--stiffness-max", type=float, default=0.9)
    ap.add_argument("--segments", default=None, help="分割数の範囲 MIN,MAX (既定は 3-6)")
    a = ap.parse_args()

    base = json.load(open(GRAVITY_SPEC if a.gravity_max > 0 else BASE_SPEC, encoding="utf-8"))
    realistic = [] if a.no_realistic else json.load(open(REALISTIC, encoding="utf-8"))
    configs = random_configs(np.random.default_rng(a.seed), a.n, a.spring_max, a.gravity_max, a.falloff_max, a.integration,
                             tuple(map(float, a.pull.split(","))), a.spring_min, a.stiffness_max)
    if a.segments:
        lo, hi = map(int, a.segments.split(","))
        seg_rng = np.random.default_rng(a.seed + 1000)
        for cfg in configs:
            cfg["rig"]["segments"] = int(seg_rng.integers(lo, hi + 1))
    cases, names = [], []
    for cfg in configs:
        rig = copy.deepcopy(base["rig"])
        rig["chains"][0].update({"segments": cfg["rig"]["segments"], "length": cfg["rig"]["length"]})
        vrm = {"stiffness": 1.0, "dragForce": 0.5, "gravityPower": 0.0, "gravityDir": [0, -1, 0], "perJoint": {}}
        for c in base["cases"]:
            cases.append({"name": f"{cfg['name']}__{c['name']}", "rig": rig, "pb": cfg["pb"],
                          "motion": scaled(c.get("motion", []), cfg["rig"]["length"] / 0.05),
                          "warmup": c.get("warmup", 0.2), "duration": c.get("duration", 2.0), "vrm": vrm})
        for c in realistic:
            r2 = copy.deepcopy(rig)
            r2.update(c.get("rig", {}))
            cases.append({"name": f"{cfg['name']}__{c['name']}", "rig": r2, "pb": cfg["pb"], "motion": c["motion"],
                          "warmup": c.get("warmup", 0.2), "duration": c.get("duration", 2.0), "vrm": vrm})
    names = [c["name"] for c in base["cases"]] + [c["name"] for c in realistic]
    rd = harness.run({"name": "collect", "fps": 60, "rigSpacing": 0.3, "writeTrace": True, "parallelCases": True, "cases": cases}, f"collect_{a.name}")
    out = os.path.join(harness.JOBS_ROOT, "maps", f"collect_{a.name}.json")
    json.dump({"result": rd, "cases": names, "configs": configs, "chain": base["rig"]["chains"][0]}, open(out, "w", encoding="utf-8"), indent=1)
    print(out, rd, len(cases), "cases")


if __name__ == "__main__":
    main()
