"""PhysBone の immobile (World / AllMotion) が Driver の並進・旋回への応答をどれだけ減らすかを測る。"""
import json, os, sys
sys.path.insert(0, os.path.dirname(__file__))
import harness

params = {
    "hair": dict(integrationType="Advanced", pull=0.358, spring=0.713, stiffness=0.386),
    "ear":  dict(integrationType="Advanced", pull=0.137, spring=0.91, stiffness=0.227),
    "simp": dict(integrationType="Simplified", pull=0.2, spring=0.2, stiffness=0.2),
}
motions = {
    "pos": [dict(type="sinePos", axis=[1, 0, 0], amplitude=0.1, frequency=1.5)],
    "turn": [dict(type="sineRot", axis=[0, 1, 0], amplitude=30, frequency=1.0)],
}
DESC = "--desc" in sys.argv
imms = [0.0, 0.25, 0.4, 0.5, 0.543, 0.6, 0.7, 0.8, 0.9, 1.0]
cases = []
for pn, p in params.items():
    for mn, m in motions.items():
        for itype in ["World", "AllMotion"]:
            for w in imms:
                if itype == "AllMotion" and w not in (0.0, 0.5, 1.0):
                    continue
                cases.append(dict(
                    name=f"{pn}__{mn}__{itype}__{w}",
                    rig=dict(rootPosition=[0, 0, -0.15], chains=[dict(segments=4, length=0.05)], avatarDescriptorOnDriver=DESC),
                    pb=dict(p, immobileType=itype, immobile=w),
                    motion=m, warmup=0.5, duration=3.0))
job = dict(name="immobile_probe", writeTrace=False, parallelCases=True, cases=cases)
rd = harness.run(job, "immobile_probe" + ("_desc" if DESC else ""))
rows = {}
for c in cases:
    s = harness.load_variants(rd, c["name"])[0]
    pn, mn, it, w = c["name"].split("__")
    rows[(pn, mn, it, float(w))] = s["pbDevMaxDeg"]
for pn in params:
    for mn in motions:
        base = rows[(pn, mn, "World", 0.0)]
        print(pn, mn, "base %.1f°" % base)
        for it in ["World", "AllMotion"]:
            print("  ", it, " ".join("%g:%.2f" % (w, rows[(pn, mn, it, w)] / base) for w in imms if (pn, mn, it, w) in rows))
print(rd)
