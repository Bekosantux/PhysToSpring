"""PhysBone の重力による静止たわみ角 (rest からの角度, summary の pbDevFinalDeg) を測る。
rest 方向の傾き (α) / gravity / gravityFalloff / pull で振る。1 分割。"""
import itertools, json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
import harness

def main():
    grav = [0.1, 0.3, 0.5, 0.7, 0.9]
    pulls = [0.1, 0.4]
    falloffs = [0.0, 0.25, 0.5, 0.75, 1.0]
    alphas = [30, 60, 90, 120, 150]   # rest と重力 (下) のなす角 [deg]
    cases = []
    for g, p, fo, a in itertools.product(grav, pulls, falloffs, alphas):
        ar = np.radians(a)
        d = [0.0, float(-np.cos(ar)), float(np.sin(ar))]
        cases.append({"name": f"g{g}_p{p}_f{fo}_a{a}",
                      "rig": {"chains": [{"segments": 1, "length": 0.05, "direction": d}]},
                      "pb": {"integrationType": "Simplified", "pull": p, "spring": 0.0, "stiffness": 0.2, "gravity": g, "gravityFalloff": fo},
                      "motion": [], "warmup": 4.0, "duration": 0.2,
                      "vrm": {"stiffness": 1.0, "dragForce": 0.5, "gravityPower": 0.0, "gravityDir": [0, -1, 0], "perJoint": {}}})
    rd = harness.run({"name": "sag", "fps": 60, "rigSpacing": 0.3, "writeTrace": False, "parallelCases": True, "cases": cases}, "sag2")
    out = {}
    for c in cases:
        s = harness.load_variants(rd, c["name"])[0]["segments"][0]
        out[c["name"]] = s["pbDevFinalDeg"]
    json.dump({"result": rd, "angles": out}, open(os.path.join(harness.JOBS_ROOT, "maps", "sag2.json"), "w"), indent=1)
    print(rd)

if __name__ == "__main__":
    main()
