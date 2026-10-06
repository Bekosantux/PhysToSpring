"""
写像 (mapping.py) を、fit に使っていないランダムな PhysBone 設定で検証する。

使い方: python validate.py MAPNAME [--n 40] [--seed 1] [--spring-max 0.7] [--gravity-max 0] [--out file]
1 ジョブで全設定 x 全ケースを並列に回し、設定ごとの誤差 (fit と同じ目的関数) を出す。
"""
import argparse
import copy
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(__file__))
import harness  # noqa: E402
from mapping import Mapping  # noqa: E402

BASE_SPEC = os.path.join(harness.JOBS_ROOT, "fitspecs", "sweep_simplified.json")
GRAVITY_SPEC = os.path.join(harness.JOBS_ROOT, "fitspecs", "sweep_gravity.json")


def random_configs(rng, n, spring_max, gravity_max, falloff_max=0.0, integration="Simplified",
                   pull_range=(0.05, 1.0), spring_min=0.0, stiffness_max=0.9):
    configs = []
    for i in range(n):
        pb = {
            "integrationType": integration,
            "pull": round(float(rng.uniform(*pull_range)), 3),
            "spring": round(float(rng.uniform(spring_min, spring_max)), 3),
            "stiffness": round(float(rng.uniform(0.0, stiffness_max)), 3) if integration == "Advanced" else 0.2,
            "gravity": round(float(rng.uniform(0.0, gravity_max)), 3) if gravity_max > 0 else 0.0,
        }
        falloff = round(float(rng.uniform(0.0, falloff_max)), 3) if falloff_max > 0 else 0.0
        rig = {"segments": int(rng.integers(3, 7)), "length": round(float(np.exp(rng.uniform(np.log(0.02), np.log(0.12)))), 4)}
        if falloff_max > 0:
            pb["gravityFalloff"] = falloff
        configs.append({"name": f"v{i:02d}", "pb": pb, "rig": rig})
    return configs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("map")
    ap.add_argument("--n", type=int, default=40)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--spring-max", type=float, default=0.7)
    ap.add_argument("--gravity-max", type=float, default=0.0)
    ap.add_argument("--integration", default="Simplified")
    ap.add_argument("--falloff-max", type=float, default=0.0)
    ap.add_argument("--segments", type=int, default=None, help="分割数を固定する")
    ap.add_argument("--scale-motion", action="store_true", help="並進の動きをボーン長 / 0.05 倍する (写像そのものの精度を見る)")
    ap.add_argument("--trace", action="store_true", help="トレースを書き出す (オフライン最適化用)")
    ap.add_argument("--converter", action="store_true", help="C# 変換器 (fromConverter) の結果も並べて Python の写像と比べる")
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    mapping = Mapping.load(args.map)
    with open(GRAVITY_SPEC if args.gravity_max > 0 else BASE_SPEC, encoding="utf-8") as f:
        base = json.load(f)
    chain0 = base["rig"]["chains"][0]
    rng = np.random.default_rng(args.seed)
    configs = random_configs(rng, args.n, args.spring_max, args.gravity_max, args.falloff_max, args.integration)
    if args.segments:
        for cfg in configs:
            cfg["rig"]["segments"] = args.segments

    cases = []
    for cfg in configs:
        rig = copy.deepcopy(base["rig"])
        rig["chains"][0].update({"segments": cfg["rig"]["segments"], "length": cfg["rig"]["length"]})
        cfg["vrm"] = mapping.vrm_spec(cfg["pb"], rig)
        cfg["vrm"]["gravityDir"] = [0, -1, 0]
        for c in base["cases"]:
            motion = copy.deepcopy(c.get("motion", []))
            if args.scale_motion:
                k = cfg["rig"]["length"] / 0.05
                for mo in motion:
                    if mo["type"] in ("step", "sinePos", "chirpPos", "rampPos"):
                        mo["amplitude"] = mo.get("amplitude", 0.1) * k
                        mo["position"] = [x * k for x in mo.get("position", [0, 0, 0])]
            cases.append({
                "name": f"{cfg['name']}__{c['name']}",
                "rig": rig, "pb": cfg["pb"], "motion": motion,
                "warmup": c.get("warmup", 0.2), "duration": c.get("duration", 2.0),
                "vrm": cfg["vrm"],
            })
            if args.converter:
                cases[-1]["vrmVariants"] = [cfg["vrm"], {"fromConverter": True}]
    job = {"name": "validate", "fps": 60, "rigSpacing": 0.3, "writeTrace": args.trace, "parallelCases": True, "cases": cases}
    result_dir = harness.run(job, f"validate_{args.map}")

    rows = []
    for cfg in configs:
        variants = {c["name"]: harness.load_variants(result_dir, f"{cfg['name']}__{c['name']}") for c in base["cases"]}
        errs = {k: v[0]["meanErrRmsDeg"] for k, v in variants.items()}
        cfg["cases"] = errs
        if args.converter:
            conv = {k: v[1]["meanErrRmsDeg"] for k, v in variants.items()}
            cfg["converterCases"] = conv
            cfg["converterDiff"] = float(max(abs(conv[k] - errs[k]) for k in errs))
            print(f"{cfg['name']}  python {np.mean(list(errs.values())):6.3f}  converter {np.mean(list(conv.values())):6.3f}  max case diff {cfg['converterDiff']:.4f}")
        cfg["err"] = float(np.mean(list(errs.values())))
        rows.append(cfg)
        print(f"{cfg['name']}  err {cfg['err']:6.3f}  pull {cfg['pb']['pull']:5.3f} spring {cfg['pb']['spring']:5.3f} stiff {cfg['pb']['stiffness']:5.3f} "
              f"grav {cfg['pb']['gravity']:5.3f} seg {cfg['rig']['segments']} len {cfg['rig']['length']:.4f}  "
              f"-> stiff {cfg['vrm']['stiffness']:.3f} drag {cfg['vrm']['dragForce']:.3f} gp {cfg['vrm']['gravityPower']:.3f}")
    errs = np.array([r["err"] for r in rows])
    print(f"mean {errs.mean():.3f}  median {np.median(errs):.3f}  p90 {np.percentile(errs, 90):.3f}  max {errs.max():.3f}  "
          f"(chain {chain0.get('direction')}, result {result_dir})")
    out = args.out or os.path.join(harness.JOBS_ROOT, "maps", f"{args.map}_validate_s{args.seed}{'_scaled' if args.scale_motion else ''}{'_fo' if args.falloff_max > 0 else ''}{'_adv' if args.integration == 'Advanced' else ''}{f'_n{args.segments}' if args.segments else ''}.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump({"map": args.map, "seed": args.seed, "result": result_dir, "configs": rows}, f, indent=2)


if __name__ == "__main__":
    main()
