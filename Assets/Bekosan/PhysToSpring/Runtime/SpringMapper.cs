using System;

namespace Bekosan.PhysToSpring
{
    public enum PbIntegration
    {
        Simplified,
        Advanced,
    }

    /// <summary>1 joint (= 1 セグメント) 分の PhysBone パラメータ。カーブ適用済みの値。</summary>
    public struct PbJointParams
    {
        public PbIntegration Integration;
        public float Pull;
        /// <summary>Simplified では spring、Advanced では momentum。</summary>
        public float Spring;
        public float Stiffness;
        public float Gravity;
        public float GravityFalloff;
    }

    public struct VrmJointParams
    {
        public float Stiffness;
        public float DragForce;
        public float GravityPower;
    }

    /// <summary>
    /// PhysBone (v1.1, 60fps) -> VRM 1.0 SpringBone のパラメータ写像。
    /// 計測ハーネスでの実測 (HarnessJobs/tools/mapping.py, maps/lerp_v5.json) から得た閉形式 + 補正。
    /// stiffness / gravityPower はボーンのワールド長 L に比例し、dragForce は L によらない。
    /// </summary>
    public static class SpringMapper
    {
        public const double Dt = 1.0 / 60.0;
        const double L0 = 0.05;
        const double StiffnessMax = 50.0;

        // Simplified の高 spring 補正 (fit_global.py simp_rc)
        //   stiffness *= 1 + a0 max(0, spring-a2)^2 / (pull+a1)
        //   (1-drag)  *= 1 - b0 max(0, spring-b2)^b3 / (1 + b1 pull)
        // 子 joint は a0, b0 だけ別の値。
        static readonly double[] SimpA = { 0.852336881226406, 0.0755693323855845, 0.45690895587299984 };
        static readonly double[] SimpB = { 0.4514774995011782, 2.24873608194168, 0.4866717660482295, 0.7839477794479663 };
        const double SimpChildA0 = 1.1858519284675966;
        const double SimpChildB0 = 0.5610390225775631;

        // Advanced (fit_global.py adv_rc3)
        //   stiffness *= exp(a k + b k^2 + c k pull + d k^2 pull + e k momentum)   (k = PhysBone の stiffness)
        //   (1-drag)  *= exp(-(f k + g k^2))   (子は f の項が k pull)
        const double AdvMomentumC = 1.374;
        static readonly double[] AdvRootStiff = { 2.0773, 0.7723, 2.2010, 2.6667, 4.9351 };
        static readonly double[] AdvChildStiff = { -1.5714, 0.5055, 2.4224, 5.5191, 4.8364 };
        static readonly double[] AdvRootKeep = { 0.0001, 16.1467 };
        static readonly double[] AdvChildKeep = { 0.0, 13.7674 };

        /// <param name="length">セグメントのワールド長 (m)。</param>
        /// <param name="isRoot">チェーン先頭 (親が物理で動かない) joint か。</param>
        /// <param name="alpha">静止姿勢でのセグメント方向と重力方向 (下) のなす角 (rad)。</param>
        /// <param name="phi">親たちの重力によるたわみの合計 (rad)。gravityFalloff 用。</param>
        public static VrmJointParams Map(PbJointParams pb, double length, bool isRoot, double alpha = Math.PI / 2, double phi = 0.0)
        {
            var pull = Clamp01(pb.Pull);
            var spring = Clamp01(pb.Spring);
            double stiff, keep;
            if (pb.Integration == PbIntegration.Advanced)
            {
                // p' = pull (1-k) が戻りの極を決め、momentum は k と pull で減衰する
                var k = Clamp01(pb.Stiffness);
                var pe = pull * (1.0 - k);
                stiff = length / Dt * (1.0 / Math.Max(1.0 - pe, 1e-3) - 1.0);
                keep = spring * (1.0 - k) / (1.0 + AdvMomentumC * k * pull / Math.Max(1.0 - pe, 1e-3));
                var s = isRoot ? AdvRootStiff : AdvChildStiff;
                stiff *= Math.Exp(s[0] * k + s[1] * k * k + s[2] * k * pull + s[3] * k * k * pull + s[4] * k * spring);
                var g = isRoot ? AdvRootKeep : AdvChildKeep;
                keep *= Math.Exp(-(g[0] * k * (isRoot ? 1.0 : pull) + g[1] * k * k));
            }
            else
            {
                // x' = lerp(rest へ pull 寄せた位置, x + 速度, spring) と VRM の更新の極を一致させる
                var r = 1.0 - pull * (1.0 - spring);
                stiff = length / Dt * (1.0 / Math.Max(r, 1e-3) - 1.0);
                keep = spring / Math.Max(r, 1e-3);
                var a0 = isRoot ? SimpA[0] : SimpChildA0;
                var b0 = isRoot ? SimpB[0] : SimpChildB0;
                stiff *= 1.0 + a0 * Sq(Math.Max(0.0, spring - SimpA[2])) / (pull + SimpA[1]);
                keep *= Math.Max(0.0, 1.0 - b0 * Math.Pow(Math.Max(0.0, spring - SimpB[2]), SimpB[3]) / (1.0 + SimpB[1] * pull));
            }
            stiff = Math.Min(stiff, StiffnessMax * length / L0);
            SplitGravity(pb, stiff, alpha, phi, out var vrmStiff, out var vrmGravity);
            return new VrmJointParams
            {
                Stiffness = (float)vrmStiff,
                DragForce = (float)Clamp01(1.0 - keep),
                GravityPower = (float)vrmGravity,
            };
        }

        /// <summary>
        /// PhysBone の重力は静止方向を ĝ = normalize((1-g) rest + g down) に変える (pull/spring によらない)。
        /// VRM の力 S' rest + G' down を ĝ 方向・大きさ S にすれば静止方向も戻り速度も一致する。
        /// </summary>
        static void SplitGravity(PbJointParams pb, double stiff, double alpha, double phi, out double vrmStiff, out double vrmGravity)
        {
            var g = EffectiveGravity(pb, alpha, phi);
            if (g <= 0.0)
            {
                vrmStiff = stiff;
                vrmGravity = 0.0;
                return;
            }
            var n = Math.Max(Math.Sqrt(Sq(1.0 - g) + g * g + 2.0 * g * (1.0 - g) * Math.Cos(alpha)), 1e-3);
            vrmStiff = stiff * (1.0 - g) / n;
            vrmGravity = stiff * g / n;
        }

        /// <summary>
        /// gravityFalloff は元の向きに近いほど重力を弱める: g_eff = g (1 - falloff max(0, cos(φ+θ)))。
        /// VRM の重力は向きによらないので、rest から出発した静止点 θ* での g_eff を使う。
        /// </summary>
        public static double EffectiveGravity(PbJointParams pb, double alpha, double phi)
        {
            var g = Clamp01(pb.Gravity);
            var falloff = Clamp01(pb.GravityFalloff);
            if (g <= 0.0 || falloff <= 0.0) return g;
            var theta = 0.0;
            for (var i = 0; i < 200; ++i)
            {
                var next = Deflection(g * (1.0 - falloff * Math.Max(0.0, Math.Cos(phi + theta))), alpha);
                if (Math.Abs(next - theta) < 1e-7) break;
                theta = next;
            }
            return g * (1.0 - falloff * Math.Max(0.0, Math.Cos(phi + theta)));
        }

        /// <summary>
        /// 重力でまっすぐなチェーンの各 joint が静止したときの (α, φ)。
        /// 子の rest は親の静止方向に沿うので α_{i+1} = α_i - θ_i。restAlphas はたわみ前の各 joint の α。
        /// </summary>
        public static void ChainAlphas(PbJointParams[] pbs, double[] restAlphas, double[] alphas, double[] phis)
        {
            var sag = 0.0;
            for (var i = 0; i < pbs.Length; ++i)
            {
                var a = restAlphas[i] - sag;
                alphas[i] = a;
                phis[i] = sag;
                sag += Deflection(EffectiveGravity(pbs[i], a, sag), a);
            }
        }

        /// <summary>重力 g で rest (下とのなす角 α) から下へ傾く角。</summary>
        static double Deflection(double g, double alpha) => Math.Atan2(g * Math.Sin(alpha), (1.0 - g) + g * Math.Cos(alpha));

        static double Clamp01(double x) => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;
        static double Sq(double x) => x * x;
    }
}
