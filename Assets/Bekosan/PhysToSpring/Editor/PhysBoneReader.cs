using System.Collections.Generic;
using System.Linq;
using UniGLTF.SpringBoneJobs;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Bekosan.PhysToSpring.Editor
{
    /// <summary>VRM の joint 1 個分の設定。</summary>
    public struct JointPlan
    {
        public VrmJointParams Params;
        public float HitRadius;
        public AnglelimitTypes LimitType;
        public float Pitch;
        public float Yaw;
        public Quaternion LimitOffset;
    }

    /// <summary>VRM の spring 1 本分の変換計画。シーンには触れない。</summary>
    public sealed class SpringPlan
    {
        public string Name;
        public VRCPhysBone Source;
        /// <summary>既存の Transform。EndpointLocal が無ければ末尾が tail。</summary>
        public readonly List<Transform> Bones = new List<Transform>();
        /// <summary>非 null なら Bones 末尾の子に {name}_end を作って tail にする (PhysBone の endpoint)。</summary>
        public Vector3? EndpointLocal;
        /// <summary>セグメントごと (= tail 以外の joint ごと)。</summary>
        public readonly List<JointPlan> Joints = new List<JointPlan>();
        public readonly List<VRCPhysBoneColliderBase> Colliders = new List<VRCPhysBoneColliderBase>();
        /// <summary>VRM spring の center。null なら center なし (immobile を再現しない)。</summary>
        public Transform Center;
    }

    /// <summary>
    /// VRCPhysBone の公開フィールドからチェーン構造とパラメータを読み、SpringPlan を作る。
    /// </summary>
    public static class PhysBoneReader
    {
        public const float MinBoneLength = 0.001f;
        /// <summary>
        /// immobile がこれ以上なら center で「親の動きを無視する」に近似する (VRM の center は 0/1 しかない)。
        /// 計測では AllMotion の応答はほぼ (1 - immobile) に比例し、World はエディタ上の Transform の移動・回転を
        /// まったく減らさない (VRChat のロコモーションだけに効くと考えられる)。中間の値で center を付けると
        /// 動きが完全に止まって見えるので、ほぼ動かない設定のときだけ center を使う。
        /// </summary>
        public const float ImmobileCenterThreshold = 0.9f;

        public static List<SpringPlan> Read(Transform avatarRoot, ConversionReport report)
        {
            var physBones = avatarRoot.GetComponentsInChildren<VRCPhysBone>(true);
            if (physBones.Length == 0)
            {
                // 変換済み (PhysBone 削除済み) のアバターを再変換して spring を空にしないよう止める
                report.Error($"{avatarRoot.name}: PhysBone が見つかりません", avatarRoot);
                return new List<SpringPlan>();
            }
            var roots = new HashSet<Transform>(physBones.Select(RootOf));
            var plans = new List<SpringPlan>();
            var owner = new Dictionary<Transform, VRCPhysBone>();
            foreach (var pb in physBones)
            {
                // 無効なコンポーネントは PhysBone では揺れない。VRM では切り替えられないので変換しない
                if (!pb.enabled)
                {
                    report.Warn($"{pb.name}: PhysBone コンポーネントが無効なので変換しません (アニメーションで有効化する場合は有効にしてから変換してください)", pb);
                    continue;
                }
                // 衣装の切り替えなどで非表示のオブジェクトは、表示されたときに揺れるよう変換する
                if (!pb.gameObject.activeInHierarchy)
                {
                    report.Info($"{pb.name}: 非アクティブなオブジェクトの PhysBone ですが変換します", pb);
                }
                var springs = ReadOne(pb, avatarRoot, roots, report);
                foreach (var plan in springs)
                {
                    // 1 つの Transform は 1 つの spring にしか入れられない
                    var dup = plan.Bones.FirstOrDefault(owner.ContainsKey);
                    if (dup != null)
                    {
                        report.Error($"{plan.Name}: {dup.name} は {owner[dup].name} の PhysBone と重複しています", pb);
                        continue;
                    }
                    foreach (var b in plan.Bones) owner[b] = pb;
                    plans.Add(plan);
                }
            }
            WarnNestedSprings(plans, report);
            return plans;
        }

        static Transform RootOf(VRCPhysBoneBase pb) => pb.rootTransform != null ? pb.rootTransform : pb.transform;

        /// <summary>
        /// immobile の近似に使う center。World はアバターの移動 (ロコモーション) を無視するのでアバタールート、
        /// AllMotion は PhysBone ルートの親の動きを無視するのでその親。
        /// </summary>
        static Transform CenterOf(VRCPhysBoneBase pb, Transform avatarRoot)
        {
            if (pb.immobile < ImmobileCenterThreshold) return null;
            return pb.immobileType == VRCPhysBoneBase.ImmobileType.World ? avatarRoot : RootOf(pb).parent;
        }

        static List<SpringPlan> ReadOne(VRCPhysBone pb, Transform avatarRoot, HashSet<Transform> pbRoots, ConversionReport report)
        {
            var result = new List<SpringPlan>();
            if (pb.version == VRCPhysBoneBase.Version.Version_1_0)
            {
                report.Error($"{pb.name}: PhysBone Version 1.0 には対応していません。Version 1.1 に変更してから変換してください", pb);
                return result;
            }
            WarnUnsupported(pb, avatarRoot, report);

            var root = RootOf(pb);
            var excluded = new HashSet<Transform>((pb.ignoreTransforms ?? new List<Transform>()).Where(t => t != null));
            if (pb.ignoreOtherPhysBones)
            {
                excluded.UnionWith(pbRoots.Where(t => t != root));
            }

            List<Transform> Children(Transform t)
            {
                var list = new List<Transform>();
                foreach (Transform c in t)
                {
                    // 前回の変換で作った end ノードはボーンとして扱わない
                    if (!excluded.Contains(c) && !SpringBoneWriter.IsGeneratedEnd(c)) list.Add(c);
                }
                return list;
            }

            // PhysBone のカーブの t は root からの深さ / 最大深さ (要計測。endpoint は数えない)
            var maxDepth = 0;
            void Measure(Transform t, int depth)
            {
                maxDepth = Mathf.Max(maxDepth, depth);
                foreach (var c in Children(t)) Measure(c, depth + 1);
            }
            Measure(root, 0);

            if (pb.multiChildType == VRCPhysBoneBase.MultiChildType.Average && HasBranch(root, Children))
            {
                report.Warn($"{pb.name}: Multi Child Type Average は First として変換します", pb);
            }

            var hasEndpoint = pb.endpointPosition != Vector3.zero;
            var chains = new List<(List<Transform> bones, bool endpoint)>();
            void Grow(Transform t, List<Transform> chain)
            {
                chain.Add(t);
                var kids = Children(t);
                if (kids.Count == 0)
                {
                    // endpoint が無い葉は回転させるセグメントが無いので tail になるだけ
                    if (chain.Count >= 2 || hasEndpoint) chains.Add((chain, hasEndpoint));
                    return;
                }
                if (kids.Count == 1)
                {
                    Grow(kids[0], chain);
                    return;
                }
                if (pb.multiChildType == VRCPhysBoneBase.MultiChildType.Ignore)
                {
                    // 分岐ボーンは動かない: ここまでを 1 本にして、子ごとに新しい spring を始める
                    if (chain.Count >= 2) chains.Add((chain, false));
                    foreach (var k in kids) Grow(k, new List<Transform>());
                    return;
                }
                // First / Average: 分岐ボーンは最初の子へ向けて回り、残りの子は別の spring
                Grow(kids[0], chain);
                foreach (var k in kids.Skip(1)) Grow(k, new List<Transform>());
            }
            Grow(root, new List<Transform>());

            if (chains.Count == 0)
            {
                report.Warn($"{pb.name}: 動かすボーンがありません (子が無く endpoint も 0)", pb);
                return result;
            }

            var depthOf = new Dictionary<Transform, int>();
            void Depths(Transform t, int depth)
            {
                depthOf[t] = depth;
                foreach (var c in Children(t)) Depths(c, depth + 1);
            }
            Depths(root, 0);

            var center = CenterOf(pb, avatarRoot);
            var colliders = pb.allowCollision != VRCPhysBoneBase.AdvancedBool.False
                && pb.colliders != null
                ? pb.colliders.Where(c => c != null).Distinct().ToList()
                : new List<VRCPhysBoneColliderBase>();

            for (var ci = 0; ci < chains.Count; ++ci)
            {
                var (bones, endpoint) = chains[ci];
                var plan = new SpringPlan
                {
                    Name = chains.Count == 1 ? pb.name : $"{pb.name}_{ci}",
                    Source = pb,
                    EndpointLocal = endpoint ? pb.endpointPosition : (Vector3?)null,
                    Center = center,
                };
                plan.Bones.AddRange(bones);
                plan.Colliders.AddRange(colliders);
                if (BuildJoints(pb, plan, depthOf, maxDepth, report)) result.Add(plan);
            }
            return result;
        }

        static bool HasBranch(Transform t, System.Func<Transform, List<Transform>> children)
        {
            var kids = children(t);
            return kids.Count > 1 || kids.Any(k => HasBranch(k, children));
        }

        static bool BuildJoints(VRCPhysBone pb, SpringPlan plan, Dictionary<Transform, int> depthOf, int maxDepth, ConversionReport report)
        {
            var n = plan.EndpointLocal.HasValue ? plan.Bones.Count : plan.Bones.Count - 1;
            var lengths = new double[n];
            var restAlphas = new double[n];
            var pbs = new PbJointParams[n];
            var ts = new float[n];
            for (var i = 0; i < n; ++i)
            {
                var head = plan.Bones[i];
                var seg = i + 1 < plan.Bones.Count
                    ? plan.Bones[i + 1].position - head.position
                    : head.TransformVector(plan.EndpointLocal.Value);
                if (seg.magnitude < MinBoneLength)
                {
                    var tail = i + 1 < plan.Bones.Count ? plan.Bones[i + 1].name : "endpoint";
                    report.Error($"{plan.Name}: {head.name} → {tail} の長さが {MinBoneLength * 1000:0}mm 未満です。長さ 0 のボーンは変換できません", head);
                    return false;
                }
                lengths[i] = seg.magnitude;
                restAlphas[i] = Vector3.Angle(seg, Vector3.down) * Mathf.Deg2Rad;
                var t = maxDepth > 0 ? (float)depthOf[head] / maxDepth : 0f;
                ts[i] = t;
                pbs[i] = new PbJointParams
                {
                    Integration = pb.integrationType == VRCPhysBoneBase.IntegrationType.Advanced ? PbIntegration.Advanced : PbIntegration.Simplified,
                    Pull = Eval(pb.pull, pb.pullCurve, t),
                    Spring = Eval(pb.spring, pb.springCurve, t),
                    Stiffness = Eval(pb.stiffness, pb.stiffnessCurve, t),
                    Gravity = Eval(pb.gravity, pb.gravityCurve, t),
                    GravityFalloff = Eval(pb.gravityFalloff, pb.gravityFalloffCurve, t),
                };
            }

            var alphas = new double[n];
            var phis = new double[n];
            SpringMapper.ChainAlphas(pbs, restAlphas, alphas, phis);

            var scale = MaxAbs(RootOf(pb).lossyScale);
            for (var i = 0; i < n; ++i)
            {
                var joint = new JointPlan
                {
                    Params = SpringMapper.Map(pbs[i], lengths[i], i == 0, alphas[i], phis[i]),
                    HitRadius = Eval(pb.radius, pb.radiusCurve, ts[i]) * scale,
                    LimitOffset = Quaternion.Euler(pb.limitRotation),
                };
                var maxX = Eval(pb.maxAngleX, pb.maxAngleXCurve, ts[i]) * Mathf.Deg2Rad;
                var maxZ = Eval(pb.maxAngleZ, pb.maxAngleZCurve, ts[i]) * Mathf.Deg2Rad;
                switch (pb.limitType)
                {
                    case VRCPhysBoneBase.LimitType.Angle:
                        joint.LimitType = AnglelimitTypes.Cone;
                        joint.Pitch = Mathf.Clamp(maxX, 0f, Mathf.PI);
                        break;
                    case VRCPhysBoneBase.LimitType.Hinge:
                        joint.LimitType = AnglelimitTypes.Hinge;
                        joint.Pitch = Mathf.Clamp(maxX, 0f, Mathf.PI);
                        break;
                    case VRCPhysBoneBase.LimitType.Polar:
                        joint.LimitType = AnglelimitTypes.Spherical;
                        joint.Pitch = Mathf.Clamp(maxX, 0f, Mathf.PI);
                        joint.Yaw = Mathf.Clamp(maxZ, 0f, Mathf.PI / 2);
                        break;
                    default:
                        joint.LimitType = AnglelimitTypes.None;
                        joint.Pitch = Mathf.PI;
                        break;
                }
                plan.Joints.Add(joint);
            }
            return true;
        }

        /// <summary>カーブがあれば値 × カーブ(t)。</summary>
        static float Eval(float value, AnimationCurve curve, float t)
        {
            return curve != null && curve.length > 0 ? value * curve.Evaluate(t) : value;
        }

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        static void WarnUnsupported(VRCPhysBone pb, Transform avatarRoot, ConversionReport report)
        {
            var ignored = new List<string>();
            if (pb.maxStretch > 0f || pb.stretchMotion > 0f) ignored.Add("Stretch");
            if (pb.maxSquish > 0f) ignored.Add("Squish");
            if (pb.allowGrabbing != VRCPhysBoneBase.AdvancedBool.False) ignored.Add("Grab");
            if (pb.allowPosing != VRCPhysBoneBase.AdvancedBool.False) ignored.Add("Pose");
            if (pb.isAnimated) ignored.Add("Is Animated");
            if (!string.IsNullOrEmpty(pb.parameter)) ignored.Add("Parameter");
            if (ignored.Count > 0) report.Info($"{pb.name}: VRM に無い機能は無視します ({string.Join(", ", ignored)})", pb);

            if (pb.immobile > 0f)
            {
                var center = CenterOf(pb, avatarRoot);
                if (center != null)
                    report.Warn($"{pb.name}: Immobile ({pb.immobileType}) {pb.immobile:0.##} は {center.name} を center にして近似します", pb);
                else
                    report.Warn($"{pb.name}: Immobile ({pb.immobileType}) {pb.immobile:0.##} は {ImmobileCenterThreshold} 未満なので無視します", pb);
            }
            if (pb.limitType == VRCPhysBoneBase.LimitType.Hinge || pb.limitType == VRCPhysBoneBase.LimitType.Polar)
            {
                report.Warn($"{pb.name}: Limit {pb.limitType} は軸の対応が未検証の近似です", pb);
            }
            if (pb.limitType != VRCPhysBoneBase.LimitType.None &&
                (pb.limitRotationXCurve?.length > 0 || pb.limitRotationYCurve?.length > 0 || pb.limitRotationZCurve?.length > 0))
            {
                report.Warn($"{pb.name}: Limit Rotation のカーブは無視します", pb);
            }
        }

        /// <summary>
        /// FastSpringBone は spring 単位で並列に計算するため、ある spring の動く joint の下に別の spring があると結果が不定になる。
        /// PhysBone の分岐をそのまま変換すると必ずこの形になるので警告だけ出す。
        /// </summary>
        static void WarnNestedSprings(List<SpringPlan> plans, ConversionReport report)
        {
            foreach (var b in plans)
            {
                var head = b.Bones[0];
                foreach (var a in plans)
                {
                    if (a == b) continue;
                    var moving = a.EndpointLocal.HasValue ? a.Bones : a.Bones.Take(a.Bones.Count - 1);
                    var parent = moving.FirstOrDefault(j => j != head && head.IsChildOf(j));
                    if (parent == null) continue;
                    report.Warn($"{b.Name} は {a.Name} の {parent.name} の下にあります。UniVRM では親子の spring の計算順が不定なため揺れ方が実行ごとに少し変わります", head);
                    break;
                }
            }
        }
    }
}
