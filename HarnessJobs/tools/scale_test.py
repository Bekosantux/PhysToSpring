"""PhysBone のスケール不変性テスト: ボーン長と移動量を同じ比率で縮め、比例スケールした VRM パラメータとの誤差を比べる。"""
import copy, json, os, sys
sys.path.insert(0, os.path.dirname(__file__))
import harness
from mapping import Mapping

base = json.load(open(os.path.join(harness.JOBS_ROOT, "fitspecs", "sweep_simplified.json"), encoding="utf-8"))
m = Mapping.load("ps_v1")
cases, meta = [], []
for p, q in [(0.2, 0.2), (0.4, 0.5)]:
    for L in [0.0125, 0.025, 0.05, 0.1]:
        for amp_scaled in [True, False]:
            k = L / 0.05
            rig = copy.deepcopy(base["rig"]); rig["chains"][0]["length"] = L
            pb = dict(base["pb"], pull=p, spring=q)
            vrm = m.vrm_spec(pb, rig)
            for c in base["cases"]:
                motion = copy.deepcopy(c["motion"])
                if amp_scaled:
                    for mo in motion:
                        if mo["type"] in ("step", "sinePos", "chirpPos", "rampPos"):
                            mo["amplitude"] = mo.get("amplitude", 0.1) * k
                            mo["position"] = [x * k for x in mo.get("position", [0, 0, 0])]
                name = f"p{p}_s{q}_L{L}_{'scaled' if amp_scaled else 'fixed'}__{c['name']}"
                cases.append({"name": name, "rig": rig, "pb": pb, "motion": motion, "warmup": c["warmup"], "duration": c["duration"], "vrm": vrm})
job = {"name": "scale_test", "fps": 60, "rigSpacing": 0.3, "writeTrace": False, "parallelCases": True, "cases": cases}
rd = harness.run(job, "scale_test")
rows = {}
for c in cases:
    key, case = c["name"].split("__")
    s = harness.load_variants(rd, c["name"])[0]
    rows.setdefault(key, {})[case] = (s["meanErrRmsDeg"], s["pbDevMaxDeg"])
for key, v in rows.items():
    print(f"{key:28s} " + "  ".join(f"{cn[:6]} err {e:5.2f} pbDev {d:5.1f}" for cn, (e, d) in v.items()))
print(rd)
