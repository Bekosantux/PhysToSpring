"""fits/{run}/*/best.json を表にする。python report.py fits/sweep_simplified_v1 [...]"""
import glob
import json
import os
import sys


def rows(fit_dir):
    for path in sorted(glob.glob(os.path.join(fit_dir, "*", "best.json"))):
        with open(path, encoding="utf-8") as f:
            best = json.load(f)
        with open(os.path.join(os.path.dirname(path), "fitspec.json"), encoding="utf-8") as f:
            spec = json.load(f)
        yield os.path.basename(os.path.dirname(path)), spec, best


def fmt(values):
    return " ".join(f"{v:6.3f}" for v in values)


def main():
    for fit_dir in sys.argv[1:]:
        print(f"== {fit_dir}")
        print(f"{'name':34s} {'gen':>3s} {'err':>6s}  {'pull':>4s} {'sprg':>4s} {'stif':>4s} {'grav':>4s} {'len':>5s} | per-joint (root -> tip)")
        for name, spec, best in rows(fit_dir):
            pb = spec["pb"]
            length = spec["rig"]["chains"][0]["length"]
            pj = best["vrm"]["perJoint"]
            print(f"{name:34s} {best['gen']:3d} {best['err']:6.3f}  {pb['pull']:4.2f} {pb['spring']:4.2f} {pb['stiffness']:4.2f} "
                  f"{pb.get('gravity', 0):4.2f} {length:5.3f} | " + "  ".join(f"{k[:4]}[{fmt(v)}]" for k, v in pj.items()))


if __name__ == "__main__":
    main()
