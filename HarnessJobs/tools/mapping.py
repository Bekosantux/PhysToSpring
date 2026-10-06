"""
PhysBone (Simplified) -> VRM SpringBone のパラメータ写像。

grid fit の結果 (pull x spring の表) を maps/{name}.json に保存し、
双線形補間 + ボーン長スケーリングで joint ごとの値を返す。

  stiffness    = S(pull, spring) * (L / L0)
  dragForce    = D(pull, spring)
  gravityPower = G(pull, spring, gravity) * (L / L0)   (重力表が無ければ 0)
"""
import glob
import json
import os

import numpy as np

MAPS_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "maps"))
L0 = 0.05
STIFFNESS_MAX = 50.0


def interp2(xs, ys, table, x, y):
    """格子 (xs, ys) 上の table[i][j] を (x, y) で双線形補間する。範囲外は端でクランプ。"""
    xs, ys, t = np.asarray(xs), np.asarray(ys), np.asarray(table)
    x = float(np.clip(x, xs[0], xs[-1]))
    y = float(np.clip(y, ys[0], ys[-1]))
    i = int(np.clip(np.searchsorted(xs, x) - 1, 0, len(xs) - 2))
    j = int(np.clip(np.searchsorted(ys, y) - 1, 0, len(ys) - 2))
    u = (x - xs[i]) / (xs[i + 1] - xs[i])
    v = (y - ys[j]) / (ys[j + 1] - ys[j])
    return float((1 - u) * (1 - v) * t[i, j] + u * (1 - v) * t[i + 1, j] + (1 - u) * v * t[i, j + 1] + u * v * t[i + 1, j + 1])


def build_ps_table(fit_dir):
    """grid fit (名前 p{pull}_s{spring}) の best.json から pull x spring 表を作る。"""
    entries = {}
    for path in glob.glob(os.path.join(fit_dir, "*", "best.json")):
        with open(path, encoding="utf-8") as f:
            b = json.load(f)
        pj = b["vrm"]["perJoint"]
        entries[(b["pb"]["pull"], b["pb"]["spring"])] = (pj["stiffness"][0], pj["dragForce"][0], b["err"])
    # pull=1, spring=0 は完全剛体で stiffness が発散するので上限で丸める (L0 で 1 フレームに 1 rad 以上戻せる値)
    entries = {k: (min(v[0], STIFFNESS_MAX), v[1], v[2]) for k, v in entries.items()}
    pulls = sorted({k[0] for k in entries})
    springs = sorted({k[1] for k in entries})
    shape = (len(pulls), len(springs))
    s, d, e = np.full(shape, np.nan), np.full(shape, np.nan), np.full(shape, np.nan)
    for (p, q), (si, di, ei) in entries.items():
        s[pulls.index(p), springs.index(q)] = si
        d[pulls.index(p), springs.index(q)] = di
        e[pulls.index(p), springs.index(q)] = ei
    return {"pull": pulls, "spring": springs, "stiffness": s.tolist(), "dragForce": d.tolist(), "err": e.tolist(), "L0": L0}


def lerp_model(pb, length, dt=1.0 / 60.0, corr=None, alpha=np.pi / 2, phi=0.0, falloff_slope=False, root=True):
    """
    計測から得た等価モデル: PhysBone (Simplified) の 1 フレーム更新は
      x' = lerp(x を rest へ pull だけ寄せた位置, x + 速度, spring)
    と同じ極を持つ。VRM の x' = normalize(x + (1-drag)v + rest*stiffness*dt) の極と一致させると
      r = 1 - pull (1 - spring),  stiffness = L/dt (1/r - 1),  dragForce = 1 - spring / r

    corr があれば spring が高い領域の補正を掛ける。VRM は長さ拘束の射影量を速度として持ち越すため、
    大きな並進の直後に PhysBone より振れが大きくなる。それを stiffness 増 / 残存速度 (1-drag) 減で打ち消す。
      stiffness *= 1 + a0 max(0, spring-a2)^2 / (pull+a1)
      (1-drag)  *= 1 - b0 max(0, spring-b2)^b3 / (1 + b1 pull)

    falloff_slope なら gravityFalloff による目標方向の動き τ(θ) を静止点で線形化し、pull を pull (1 - dτ/dθ) にする。
    """
    pull = float(np.clip(pb["pull"], 0.0, 1.0))
    if falloff_slope:
        pull *= 1.0 - target_slope(pb, alpha, phi)
    spring = float(np.clip(pb["spring"], 0.0, 1.0))
    if pb.get("integrationType") == "Advanced":
        return advanced_model(pb, pull, spring, length, dt, alpha, phi, root)
    r = 1.0 - pull * (1.0 - spring)
    stiff = length / dt * (1.0 / max(r, 1e-3) - 1.0)
    keep = spring / max(r, 1e-3)
    if corr:
        a, b = corr["a"], corr["b"]
        # childScale があれば子 joint は補正の強さ (a0, b0) を変える (fit_global.py simp_rc)
        a0, b0 = (a[0], b[0]) if root or "childScale" not in corr else corr["childScale"]
        stiff *= 1.0 + a0 * max(0.0, spring - a[2]) ** 2 / (pull + a[1])
        keep *= max(0.0, 1.0 - b0 * max(0.0, spring - b[2]) ** b[3] / (1.0 + b[1] * pull))
    stiff = min(stiff, STIFFNESS_MAX * length / L0)
    stiff, grav = split_gravity(pb, stiff, alpha, phi)
    return {"stiffness": stiff, "dragForce": float(np.clip(1.0 - keep, 0.0, 1.0)), "gravityPower": grav}


ADV_MOMENTUM_C = 1.374
# 全体フィット (fit_global.py adv_rc3, train 120 config / test 40 config, test 10.38° -> 5.76°) の補正係数。
# stiffness 補正 exp(a k + b k^2 + c k pull + d k^2 pull + e k momentum) を root / 子 別に、
# keep 補正 exp(-(f k + g k^2)) (子は f の項が k pull)。keep は補正前より大きくならない。
ADV_ROOT_STIFF = (2.0773, 0.7723, 2.2010, 2.6667, 4.9351)
ADV_CHILD_STIFF = (-1.5714, 0.5055, 2.4224, 5.5191, 4.8364)
ADV_ROOT_KEEP = (0.0001, 16.1467)
ADV_CHILD_KEEP = (0.0, 13.7674)


def advanced_model(pb, pull, momentum, length, dt, alpha, phi, root=True):
    """
    Advanced (自由振動を VRM の線形モデルに直接当てた結果から):
      p' = pull (1 - stiffness)            ... 戻りの極は 1 - p' (momentum 0 で厳密)
      stiffness = L/dt (1/(1-p') - 1)
      1-drag = momentum (1-k) / (1 + c k pull / (1-p'))   (k = PhysBone の stiffness, c ≈ 1.374)
    PhysBone の stiffness は親の回転/移動に対しても向きを保つが VRM は先端位置を保つため、
    チェーンでは root と子で別の補正を掛ける (keep は補正前を超えないので発散しない)。
    """
    k = float(np.clip(pb.get("stiffness", 0.0), 0.0, 1.0))
    pe = pull * (1.0 - k)
    stiff = length / dt * (1.0 / max(1.0 - pe, 1e-3) - 1.0)
    keep = momentum * (1.0 - k) / (1.0 + ADV_MOMENTUM_C * k * pull / max(1.0 - pe, 1e-3))
    a, b, c, d, e = ADV_ROOT_STIFF if root else ADV_CHILD_STIFF
    stiff *= float(np.exp(a * k + b * k * k + c * k * pull + d * k * k * pull + e * k * momentum))
    f, g = ADV_ROOT_KEEP if root else ADV_CHILD_KEEP
    keep *= float(np.exp(-(f * k * (1.0 if root else pull) + g * k * k)))
    stiff = min(stiff, STIFFNESS_MAX * length / L0)
    stiff, grav = split_gravity(pb, stiff, alpha, phi)
    return {"stiffness": stiff, "dragForce": float(np.clip(1.0 - keep, 0.0, 1.0)), "gravityPower": grav}


def effective_gravity(pb, alpha=np.pi / 2, phi=0.0):
    """
    gravityFalloff は今の向きが rest に近いほど重力を弱める (計測で一致):
      g_eff = g (1 - falloff max(0, cos(φ+θ)))
    θ は親から見た rest からのたわみ、φ は親たちのたわみの合計 (ワールドでの元の向きからのずれは φ+θ)。
    VRM の重力は向きに依存できないので、rest から出発した静止点 θ* での g_eff を定数として使う。
    θ ↦ ĝ(g_eff(θ)) の角は θ について単調増加なので、0 からの反復は最小の不動点に単調に収束する。
    """
    g = float(np.clip(pb.get("gravity", 0.0), 0.0, 1.0))
    falloff = float(np.clip(pb.get("gravityFalloff", 0.0), 0.0, 1.0))
    if g <= 0.0 or falloff <= 0.0:
        return g
    theta = 0.0
    for _ in range(200):
        ge = g * (1.0 - falloff * max(0.0, np.cos(phi + theta)))
        nxt = float(np.arctan2(ge * np.sin(alpha), (1.0 - ge) + ge * np.cos(alpha)))
        if abs(nxt - theta) < 1e-7:
            break
        theta = nxt
    return g * (1.0 - falloff * max(0.0, np.cos(phi + theta)))


def target_angle(pb, alpha, phi, theta):
    """親から見たたわみ θ にいるときの PhysBone の目標方向 ĝ の角 (rest から)。"""
    g = float(np.clip(pb.get("gravity", 0.0), 0.0, 1.0))
    falloff = float(np.clip(pb.get("gravityFalloff", 0.0), 0.0, 1.0))
    ge = g * (1.0 - falloff * max(0.0, np.cos(phi + theta)))
    return float(np.arctan2(ge * np.sin(alpha), (1.0 - ge) + ge * np.cos(alpha)))


def target_slope(pb, alpha=np.pi / 2, phi=0.0):
    """静止点 θ* での dτ/dθ (falloff が無ければ 0)。"""
    if pb.get("gravity", 0.0) <= 0.0 or pb.get("gravityFalloff", 0.0) <= 0.0:
        return 0.0
    ge = effective_gravity(pb, alpha, phi)
    theta = float(np.arctan2(ge * np.sin(alpha), (1.0 - ge) + ge * np.cos(alpha)))
    h = 1e-4
    slope = (target_angle(pb, alpha, phi, theta + h) - target_angle(pb, alpha, phi, theta - h)) / (2 * h)
    return float(np.clip(slope, 0.0, 0.95))


def split_gravity(pb, stiff, alpha=np.pi / 2, phi=0.0):
    """
    PhysBone の重力は pull/spring によらず静止方向を ĝ = normalize((1-g) rest + g down) に変える (計測で一致)。
    VRM の力 S' rest + G' down を ĝ 方向・大きさ S にすれば、静止方向も ĝ まわりの戻り速度も一致する:
      S' = S (1-g) / n,  G' = S g / n,  n = |(1-g) rest + g down|
    alpha は rest 方向と重力方向のなす角 (水平なボーンで π/2)。
    """
    g = effective_gravity(pb, alpha, phi)
    if g <= 0.0:
        return stiff, 0.0
    n = float(np.sqrt((1.0 - g) ** 2 + g ** 2 + 2.0 * g * (1.0 - g) * np.cos(alpha)))
    n = max(n, 1e-3)
    return stiff * (1.0 - g) / n, stiff * g / n


class Mapping:
    def __init__(self, table):
        self.t = table

    @classmethod
    def load(cls, name):
        with open(os.path.join(MAPS_DIR, name + ".json"), encoding="utf-8") as f:
            return cls(json.load(f))

    def save(self, name):
        os.makedirs(MAPS_DIR, exist_ok=True)
        with open(os.path.join(MAPS_DIR, name + ".json"), "w", encoding="utf-8") as f:
            json.dump(self.t, f, indent=2)

    def joint(self, pb, length):
        t = self.t
        if t.get("model") == "lerp":
            return lerp_model(pb, length, t.get("dt", 1.0 / 60.0), t.get("corr"))
        k = length / t.get("L0", L0)
        pull, spring = pb["pull"], pb["spring"]
        # 剛性は桁が変わるので log 空間で補間する
        stiff = float(np.exp(interp2(t["pull"], t["spring"], np.log(t["stiffness"]), pull, spring))) * k
        if t.get("dragInterp") == "log1m":
            # 1 - drag (1 フレームで残る速度の割合) は 1 付近で桁が変わるので log 空間で補間する
            drag = 1.0 - float(np.exp(interp2(t["pull"], t["spring"], np.log(np.maximum(1.0 - np.asarray(t["dragForce"]), 1e-3)), pull, spring)))
        else:
            drag = interp2(t["pull"], t["spring"], t["dragForce"], pull, spring)
        grav = 0.0
        if pb.get("gravity", 0) > 0 and "gravity" in t:
            g = t["gravity"]
            # G / gravity を (pull, gravity) で補間 (spring は gravity 表の基準値のみ)
            ratio = interp2(g["pull"], g["gravity"], g["ratio"], pull, pb["gravity"])
            grav = ratio * pb["gravity"] * k
        return {"stiffness": stiff, "dragForce": drag, "gravityPower": grav}

    def vrm_spec(self, pb, rig):
        """チェーン 0 の長さで joint 値を決める (一様チェーン前提)。"""
        chain = rig["chains"][0]
        length = chain["length"]
        j = self.joint(pb, length)
        spec = {"stiffness": j["stiffness"], "dragForce": j["dragForce"], "gravityPower": j["gravityPower"], "perJoint": {}}
        adv = pb.get("integrationType") == "Advanced"
        rc = adv or "childScale" in (self.t.get("corr") or {})
        if self.t.get("model") == "lerp" and (pb.get("gravity", 0) > 0 or rc):
            # 重力ありは joint ごとに静止姿勢での α を使う。Advanced / childScale ありは root と子で値が違う
            d = np.asarray(chain.get("direction", [0, -1, 0]), dtype=float)
            alpha0 = float(np.arccos(np.clip(np.dot(d / np.linalg.norm(d), [0, -1, 0]), -1.0, 1.0)))
            alphas = chain_alphas(pb, alpha0, chain["segments"]) if pb.get("gravity", 0) > 0 else [(np.pi / 2, 0.0)] * chain["segments"]
            joints = [lerp_model(pb, length, self.t.get("dt", 1.0 / 60.0), self.t.get("corr"), a, phi, self.t.get("falloffSlope", False), i == 0)
                      for i, (a, phi) in enumerate(alphas)]
            spec["perJoint"] = {k: [jj[k] for jj in joints] for k in ("stiffness", "gravityPower", "dragForce")}
        return spec


def chain_alphas(pb, alpha0, segments):
    """
    まっすぐなチェーンの各 joint について、PhysBone の重力静止姿勢での「rest 方向と下向きのなす角」を返す。
    子の rest は親の静止方向に沿うので α_{i+1} = α_i - θ_i (θ_i は joint i のたわみ)。
    VRM では stiffness 項だけが親と一緒に回るので、力の大きさ n(α) は実行時の α で決まる。
    (α_i, φ_i) を返す。φ_i は親たちのたわみの合計 (falloff 用)。
    """
    out, a = [], alpha0
    for _ in range(segments):
        phi = alpha0 - a
        out.append((a, phi))
        ge = effective_gravity(pb, a, phi)
        a -= float(np.arctan2(ge * np.sin(a), (1.0 - ge) + ge * np.cos(a)))
    return out
