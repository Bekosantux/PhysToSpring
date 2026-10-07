"""
PhysBone Version 1.0 と 1.1 を同じ設定・同じ動きで並べ、挙動の違いを測る。
VRM 側は 1.1 用の写像 (lerp_v5) なので、summary の誤差は「1.1 用写像を 1.0 に当てたときの誤差」になる。

使い方:
  python version_probe.py build [--out job.json]   ジョブを書き出す (watcher があれば --submit で投入して待つ)
  python version_probe.py analyze RESULT_DIR
  python version_probe.py equiv V10_RESULT_DIR      v1.0 の gravity g を v1.1 の g/(g+pull) に置き換えて、v1.0 のトレースと比べる
"""
import argparse, copy, csv, json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
import harness
from mapping import Mapping
from validate import random_configs, BASE_SPEC, GRAVITY_SPEC

VERSIONS = {"v10": "Version_1_0", "v11": "Version_1_1"}
GROUPS = [
    # (名前, integration, n, spring_max, gravity_max, falloff_max, seed)
    ("simp", "Simplified", 10, 0.7, 0.0, 0.0, 101),
    ("simp_grav", "Simplified", 8, 0.7, 0.9, 1.0, 102),
    ("adv", "Advanced", 10, 0.9, 0.0, 0.0, 103),
    ("adv_grav", "Advanced", 8, 0.9, 0.9, 1.0, 104),
]
SAG_GRAV = [0.2, 0.5, 0.8]
SAG_PULL = [0.1, 0.4, 0.8]
SAG_ALPHA = [30, 90, 150]
SAG_STIFF = [0.0, 0.5]   # Advanced のみ


def build():
    mapping = Mapping.load("lerp_v5")
    cases, meta = [], {"groups": {}, "sag": []}
    for gname, integ, n, smax, gmax, fmax, seed in GROUPS:
        base = json.load(open(GRAVITY_SPEC if gmax > 0 else BASE_SPEC, encoding="utf-8"))
        configs = random_configs(np.random.default_rng(seed), n, smax, gmax, fmax, integ)
        meta["groups"][gname] = {"configs": configs, "cases": [c["name"] for c in base["cases"]]}
        for cfg in configs:
            rig = copy.deepcopy(base["rig"])
            rig["chains"][0].update({"segments": cfg["rig"]["segments"], "length": cfg["rig"]["length"]})
            vrm = mapping.vrm_spec(cfg["pb"], rig)
            vrm["gravityDir"] = [0, -1, 0]
            k = cfg["rig"]["length"] / 0.05
            for c in base["cases"]:
                motion = copy.deepcopy(c.get("motion", []))
                for mo in motion:
                    if mo["type"] in ("step", "sinePos", "chirpPos", "rampPos"):
                        mo["amplitude"] = mo.get("amplitude", 0.1) * k
                        mo["position"] = [x * k for x in mo.get("position", [0, 0, 0])]
                for vk, vv in VERSIONS.items():
                    cases.append({"name": f"{gname}__{cfg['name']}__{c['name']}__{vk}", "rig": rig,
                                  "pb": dict(cfg["pb"], version=vv), "motion": motion,
                                  "warmup": c.get("warmup", 0.2), "duration": c.get("duration", 2.0), "vrm": vrm})
    # 重力の静止たわみ (1 分割、spring/momentum 0)
    for integ in ("Simplified", "Advanced"):
        for st in (SAG_STIFF if integ == "Advanced" else [0.2]):
            for g in SAG_GRAV:
                for p in SAG_PULL:
                    for a in SAG_ALPHA:
                        ar = np.radians(a)
                        d = [0.0, float(-np.cos(ar)), float(np.sin(ar))]
                        tag = f"sag__{integ[:4]}_k{st}_g{g}_p{p}_a{a}"
                        meta["sag"].append({"tag": tag, "integ": integ, "stiffness": st, "gravity": g, "pull": p, "alpha": a})
                        for vk, vv in VERSIONS.items():
                            cases.append({"name": f"{tag}__{vk}",
                                          "rig": {"chains": [{"segments": 1, "length": 0.05, "direction": d}]},
                                          "pb": {"version": vv, "integrationType": integ, "pull": p, "spring": 0.0, "stiffness": st, "gravity": g},
                                          "motion": [], "warmup": 4.0, "duration": 0.2,
                                          "vrm": {"stiffness": 1.0, "dragForce": 0.5, "gravityPower": 0.0, "gravityDir": [0, -1, 0], "perJoint": {}}})
    return {"name": "version_probe", "fps": 60, "rigSpacing": 0.3, "writeTrace": True, "parallelCases": True, "cases": cases}, meta


def node_dirs(path):
    """pb リグの各ノード→次ノードの方向 (Driver 空間) を [frame, seg, 3] で返す。"""
    pos = {}
    for r in csv.DictReader(open(path)):
        if r["rig"] == "pb":
            pos.setdefault(r["bone"], []).append([float(r["px"]), float(r["py"]), float(r["pz"])])
    names = ["Root"] + sorted([b for b in pos if b.startswith("C0_") and not b.endswith("_end")], key=lambda b: int(b[3:]))
    P = np.array([pos[b] for b in names]).transpose(1, 0, 2)
    D = P[:, 1:] - P[:, :-1]
    return D / np.linalg.norm(D, axis=2, keepdims=True)


def ang(a, b):
    return np.degrees(np.arccos(np.clip(np.sum(a * b, axis=-1), -1, 1)))


def analyze(rd, meta):
    print("=== 同じ設定での v1.0 と v1.1 の差 (全ボーン・全フレームの角度差) と、1.1 用写像の誤差 ===")
    out = {"result": rd, "groups": {}, "sag": []}
    for gname, g in meta["groups"].items():
        print(f"\n[{gname}]  config: pull spring stiff grav falloff | 各ケースの v10-v11 角度差 rms (deg) | 写像誤差 v11 → v10")
        rows = []
        for cfg in g["configs"]:
            diffs, e10, e11 = {}, [], []
            for c in g["cases"]:
                n = f"{gname}__{cfg['name']}__{c}"
                d10 = node_dirs(os.path.join(rd, n + "__v10", "trace.csv"))
                d11 = node_dirs(os.path.join(rd, n + "__v11", "trace.csv"))
                diffs[c] = float(np.sqrt(np.mean(ang(d10, d11) ** 2)))
                e10.append(harness.load_variants(rd, n + "__v10")[0]["meanErrRmsDeg"])
                e11.append(harness.load_variants(rd, n + "__v11")[0]["meanErrRmsDeg"])
            pb = cfg["pb"]
            rows.append({"cfg": cfg, "diff": diffs, "err10": float(np.mean(e10)), "err11": float(np.mean(e11))})
            print(f"  {cfg['name']} {pb['pull']:.2f} {pb['spring']:.2f} {pb['stiffness']:.2f} {pb['gravity']:.2f} {pb.get('gravityFalloff', 0):.2f} seg{cfg['rig']['segments']} | "
                  + " ".join(f"{c}:{v:6.2f}" for c, v in diffs.items()) + f" | {rows[-1]['err11']:6.2f} → {rows[-1]['err10']:6.2f}")
        allv = [v for r in rows for v in r["diff"].values()]
        print(f"  平均差 {np.mean(allv):.3f}°  最大 {np.max(allv):.3f}°  写像誤差平均 v11 {np.mean([r['err11'] for r in rows]):.2f}° / v10 {np.mean([r['err10'] for r in rows]):.2f}°")
        out["groups"][gname] = rows
    print("\n=== 重力の静止たわみ角 (1 分割, rest からの角度 deg)  v10 / v11 ===")
    for s in meta["sag"]:
        f = [harness.load_variants(rd, f"{s['tag']}__{v}")[0]["segments"][0]["pbDevFinalDeg"] for v in VERSIONS]
        s["v10"], s["v11"] = f
        out["sag"].append(s)
        print(f"  {s['integ'][:4]} k{s['stiffness']} g{s['gravity']} p{s['pull']} a{s['alpha']:3d}:  {f[0]:7.2f} / {f[1]:7.2f}")
    path = os.path.join(harness.JOBS_ROOT, "maps", "version_probe.json")
    json.dump(out, open(path, "w", encoding="utf-8"), indent=1)
    print(path)


def g_equiv(pb):
    g, p = pb.get("gravity", 0.0), pb["pull"]
    return g / (g + p) if g > 0 else 0.0


def equiv(rd10):
    """1.0 → 1.1 の gravity 換算 g' = g / (g + pull) で、1.1 が 1.0 の動きを再現するか。"""
    meta = json.load(open(os.path.join(harness.JOBS_ROOT, "maps", "version_probe_meta.json"), encoding="utf-8"))
    job10 = json.load(open(os.path.join(rd10, "job.json"), encoding="utf-8"))
    cases = []
    for c in job10["cases"]:
        if c["pb"]["version"] == "Version_1_0" and c["pb"].get("gravity", 0) > 0 and not c["name"].startswith("sag__"):
            c = copy.deepcopy(c)
            c["name"] = c["name"][:-5] + "__eq"
            c["pb"].update(version="Version_1_1", gravity=round(g_equiv(c["pb"]), 6))
            cases.append(c)
    # falloff の効き方を 1 分割の静止たわみで見る
    sag = []
    for fo in (0.0, 0.5, 1.0):
        for g in (0.3, 0.7):
            for p in (0.2, 0.6):
                for a in (60, 90, 150):
                    ar = np.radians(a)
                    d = [0.0, float(-np.cos(ar)), float(np.sin(ar))]
                    tag = f"fo{fo}_g{g}_p{p}_a{a}"
                    sag.append(tag)
                    for vk, vv, gg in (("v10", "Version_1_0", g), ("eq", "Version_1_1", g / (g + p))):
                        cases.append({"name": f"fsag__{tag}__{vk}", "rig": {"chains": [{"segments": 1, "length": 0.05, "direction": d}]},
                                      "pb": {"version": vv, "integrationType": "Simplified", "pull": p, "spring": 0.0, "gravity": gg, "gravityFalloff": fo},
                                      "motion": [], "warmup": 4.0, "duration": 0.2,
                                      "vrm": {"stiffness": 1.0, "dragForce": 0.5, "gravityPower": 0.0, "gravityDir": [0, -1, 0], "perJoint": {}}})
    rd = harness.run({"name": "version_equiv", "fps": 60, "rigSpacing": 0.3, "writeTrace": True, "parallelCases": True, "cases": cases}, "version_equiv")
    print("=== v1.0 (g) と v1.1 (g' = g/(g+pull)) の差 (deg rms) ===")
    for gname in ("simp_grav", "adv_grav"):
        g = meta["groups"][gname]
        allv = []
        for cfg in g["configs"]:
            if cfg["pb"].get("gravity", 0) <= 0:
                continue
            ds = []
            for c in g["cases"]:
                n = f"{gname}__{cfg['name']}__{c}"
                ds.append(float(np.sqrt(np.mean(ang(node_dirs(os.path.join(rd10, n + "__v10", "trace.csv")), node_dirs(os.path.join(rd, n + "__eq", "trace.csv"))) ** 2))))
            allv += ds
            pb = cfg["pb"]
            print(f"  {gname} {cfg['name']} pull {pb['pull']:.2f} spr {pb['spring']:.2f} k {pb['stiffness']:.2f} g {pb['gravity']:.2f}→{g_equiv(pb):.3f} fo {pb.get('gravityFalloff', 0):.2f}: "
                  + " ".join(f"{c}:{d:5.2f}" for c, d in zip(g["cases"], ds)))
        print(f"  {gname} 平均 {np.mean(allv):.3f}°  最大 {np.max(allv):.3f}°")
    print("=== falloff 込みの静止たわみ  v10 / v11(g') ===")
    for tag in sag:
        f = [harness.load_variants(rd, f"fsag__{tag}__{v}")[0]["segments"][0]["pbDevFinalDeg"] for v in ("v10", "eq")]
        print(f"  {tag}: {f[0]:7.2f} / {f[1]:7.2f}  diff {f[0] - f[1]:+.2f}")
    print(rd)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("cmd", choices=["build", "analyze", "equiv"])
    ap.add_argument("result", nargs="?")
    ap.add_argument("--out", default=os.path.join(harness.JOBS_ROOT, "version_probe_job.json"))
    ap.add_argument("--submit", action="store_true")
    a = ap.parse_args()
    meta_path = os.path.join(harness.JOBS_ROOT, "maps", "version_probe_meta.json")
    if a.cmd == "build":
        job, meta = build()
        json.dump(meta, open(meta_path, "w", encoding="utf-8"), indent=1)
        json.dump(job, open(a.out, "w", encoding="utf-8"))
        print(a.out, len(job["cases"]), "cases")
        if a.submit:
            analyze(harness.run(job, "version_probe"), meta)
    elif a.cmd == "equiv":
        equiv(a.result)
    else:
        analyze(a.result, json.load(open(meta_path, encoding="utf-8")))


if __name__ == "__main__":
    main()
