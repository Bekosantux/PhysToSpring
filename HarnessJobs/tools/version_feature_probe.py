"""
PhysBone Version 1.0 と 1.1 で、コライダー / Limit / Immobile の効き方が同じかを測る。
同じ設定・同じ動きで 1.0 と 1.1 を並べ、ノード方向の角度差 (全ボーン・全フレームの rms) を見る。
機能なし (none) の同じ動きとの差も出して、機能が実際に効いていることを確かめる。
VRM 側は変換器 (fromConverter) の結果なので、summary の誤差は各バージョンでの変換誤差になる。

使い方:
  python version_feature_probe.py            ジョブを実行して解析
  python version_feature_probe.py RESULT_DIR 解析だけ
"""
import copy, json, os, sys
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
import harness
from version_probe import node_dirs, ang

VERSIONS = {"v10": "Version_1_0", "v11": "Version_1_1"}
INTEGRATIONS = {
    "simp": dict(integrationType="Simplified", pull=0.2, spring=0.3, stiffness=0.0),
    "adv0": dict(integrationType="Advanced", pull=0.2, spring=0.5, stiffness=0.0),
    # stiffness > 0 は機能なしでも 1.0 と 1.1 が違うので、none との差の増え方を見る
    "adv3": dict(integrationType="Advanced", pull=0.2, spring=0.5, stiffness=0.3),
}
MOTIONS = {
    "pos": [dict(type="sinePos", axis=[1, 0, 0], amplitude=0.1, frequency=1.5)],
    "rot": [dict(type="sineRot", axis=[0, 0, 1], amplitude=40, frequency=1.0)],
}
# (名前, pb の上書き, PB コライダー, 使う動き)
FEATURES = [
    ("none", {}, [], ["pos", "rot"]),
    ("sphere", dict(radius=0.01), [dict(attach="Base", shape="Sphere", radius=0.03, position=[0.06, -0.15, 0])], ["pos"]),
    ("sphere_r2", dict(radius=0.02), [dict(attach="Base", shape="Sphere", radius=0.03, position=[0.06, -0.15, 0])], ["pos"]),
    ("capsule", dict(radius=0.01), [dict(attach="Base", shape="Capsule", radius=0.025, height=0.2, position=[0.06, -0.12, 0])], ["pos"]),
    ("capsule_bas", dict(radius=0.01), [dict(attach="Base", shape="Capsule", radius=0.025, height=0.2, position=[0.06, -0.12, 0], bonesAsSpheres=True)], ["pos"]),
    ("plane", dict(radius=0.01), [dict(attach="Base", shape="Plane", position=[0.04, 0, 0], rotationEuler=[0, 0, 90])], ["pos"]),
    ("inside", dict(radius=0.01), [dict(attach="Base", shape="Sphere", radius=0.16, position=[0, -0.1, 0], insideBounds=True)], ["pos"]),
    ("drv_sphere", dict(radius=0.01), [dict(attach="Driver", shape="Sphere", radius=0.03, position=[0.05, -0.12, 0])], ["rot"]),
    ("angle20", dict(limitType="Angle", maxAngleX=20), [], ["pos", "rot"]),
    ("angle20_rot", dict(limitType="Angle", maxAngleX=20, limitRotation=[0, 0, 25]), [], ["pos"]),
    ("hinge30", dict(limitType="Hinge", maxAngleX=30), [], ["pos", "rot"]),
    ("polar", dict(limitType="Polar", maxAngleX=30, maxAngleZ=10), [], ["pos", "rot"]),
    ("imm_all05", dict(immobileType="AllMotion", immobile=0.5), [], ["pos", "rot"]),
    ("imm_all1", dict(immobileType="AllMotion", immobile=1.0), [], ["pos", "rot"]),
    ("imm_world05", dict(immobileType="World", immobile=0.5), [], ["pos"]),
    ("imm_world1", dict(immobileType="World", immobile=1.0), [], ["pos"]),
]
RIG = dict(chains=[dict(segments=4, length=0.05, direction=[0, -1, 0])])


def build():
    cases = []
    for iname, ipb in INTEGRATIONS.items():
        for fname, fpb, cols, motions in FEATURES:
            for mname in motions:
                for vk, vv in VERSIONS.items():
                    cases.append(dict(name=f"{iname}__{fname}__{mname}__{vk}", rig=RIG,
                                      pb=dict(ipb, **fpb, version=vv, gravity=0.0), colliders=cols,
                                      motion=MOTIONS[mname], warmup=0.3, duration=3.0,
                                      vrm={"fromConverter": True, "gravityDir": [0, -1, 0]}))
    return dict(name="version_feature_probe", fps=60, rigSpacing=0.5, writeTrace=True, parallelCases=True, cases=cases)


def rms(a):
    return float(np.sqrt(np.mean(a ** 2)))


def analyze(rd):
    out = []
    print("機能別  v10-v11 差 rms (deg) | 機能の効き = none との差 rms (v10 / v11) | 変換後 VRM の誤差 (v10 / v11)")
    for iname in INTEGRATIONS:
        print(f"[{iname}]")
        for fname, _, _, motions in FEATURES:
            for mname in motions:
                n = f"{iname}__{fname}__{mname}"
                d = {vk: node_dirs(os.path.join(rd, f"{n}__{vk}", "trace.csv")) for vk in VERSIONS}
                base = {vk: node_dirs(os.path.join(rd, f"{iname}__none__{mname}__{vk}", "trace.csv")) for vk in VERSIONS}
                err = {vk: harness.load_variants(rd, f"{n}__{vk}")[0]["meanErrRmsDeg"] for vk in VERSIONS}
                row = dict(integ=iname, feature=fname, motion=mname,
                           diff=rms(ang(d["v10"], d["v11"])),
                           diffMax=float(np.max(ang(d["v10"], d["v11"]))),
                           effect={vk: rms(ang(d[vk], base[vk])) for vk in VERSIONS},
                           vrmErr=err)
                out.append(row)
                print(f"  {fname:12s} {mname:3s}  diff {row['diff']:6.3f} (max {row['diffMax']:6.2f}) | "
                      f"effect {row['effect']['v10']:6.2f} / {row['effect']['v11']:6.2f} | vrm {err['v10']:6.2f} / {err['v11']:6.2f}")
    path = os.path.join(harness.JOBS_ROOT, "maps", "version_feature_probe.json")
    json.dump({"result": rd, "rows": out}, open(path, "w", encoding="utf-8"), indent=1)
    print(path)


if __name__ == "__main__":
    if len(sys.argv) > 1:
        analyze(sys.argv[1])
    else:
        job = build()
        print(len(job["cases"]), "cases")
        analyze(harness.run(job, "version_feature_probe"))
