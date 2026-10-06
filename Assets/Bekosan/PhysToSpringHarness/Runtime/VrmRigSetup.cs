using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF.SpringBoneJobs;
using UniGLTF.SpringBoneJobs.Blittables;
using UniGLTF.SpringBoneJobs.InputPorts;
using UniVRM10;
using UnityEngine;

namespace Bekosan.PhysToSpring.Harness
{
    /// <summary>
    /// 計測用リグに VRM10SpringBoneJoint / Collider / ColliderGroup を付け、
    /// FastSpringBone へ渡す入力を組み立てる。
    /// </summary>
    public static class VrmRigSetup
    {
        public static FastSpringBoneSpring[] Setup(Rig rig, HarnessCase c, VrmSpec spec)
        {
            var springNames = spec.springs ?? AutoSprings(rig, c.pb.multiChildType);
            var center = rig.Find(spec.center);

            var group = rig.Base.gameObject.AddComponent<VRM10SpringBoneColliderGroup>();
            group.Name = "harness";
            var colliderSpecs = spec.colliders ?? c.colliders.Select(ConvertCollider).ToList();
            foreach (var cs in colliderSpecs)
            {
                group.Colliders.Add(AddCollider(rig, cs));
            }
            var colliders = group.Colliders.Select(col => new FastSpringBoneCollider
            {
                Transform = col.transform,
                Collider = new BlittableCollider(
                    offset: col.Offset,
                    radius: col.Radius,
                    tailOrNormal: col.TailOrNormal,
                    colliderType: ToBlittable(col.ColliderType)),
            }).ToArray();

            var springs = new List<FastSpringBoneSpring>();
            for (var si = 0; si < springNames.Count; ++si)
            {
                var names = springNames[si];
                if (names.Count < 2) continue;
                var joints = new FastSpringBoneJoint[names.Count];
                for (var i = 0; i < names.Count; ++i)
                {
                    var t = rig.Find(names[i]);
                    var joint = t.GetComponent<VRM10SpringBoneJoint>();
                    if (joint != null) throw new ArgumentException($"joint {names[i]} belongs to multiple springs");
                    joint = t.gameObject.AddComponent<VRM10SpringBoneJoint>();
                    ApplyJoint(joint, spec, si, i);
                    joints[i] = new FastSpringBoneJoint
                    {
                        Transform = t,
                        Joint = joint.Blittable,
                        DefaultLocalRotation = t.localRotation,
                    };
                }
                springs.Add(new FastSpringBoneSpring { center = center, joints = joints, colliders = colliders });
            }
            return springs.ToArray();
        }

        /// <summary>
        /// ある spring が回転させる joint (末尾以外) が別の spring の先頭 joint の祖先になっている組を返す。
        /// FastSpringBone は spring 単位で並列に計算するため、この場合は子側の結果が実行ごとに変わる。
        /// </summary>
        public static List<string> NestedSpringWarnings(FastSpringBoneSpring[] springs)
        {
            var warnings = new List<string>();
            for (var a = 0; a < springs.Length; ++a)
            {
                var ja = springs[a].joints;
                for (var b = 0; b < springs.Length; ++b)
                {
                    if (a == b) continue;
                    var head = springs[b].joints[0].Transform;
                    for (var i = 0; i < ja.Length - 1; ++i)
                    {
                        if (head == ja[i].Transform || !head.IsChildOf(ja[i].Transform)) continue;
                        warnings.Add($"spring {b} ({head.name}) is under joint {ja[i].Transform.name} of spring {a}; FastSpringBone result is nondeterministic");
                        break;
                    }
                }
            }
            return warnings;
        }

        /// <summary>
        /// PhysBone の chain 構造に合わせた spring 分割。
        /// 単一チェーン: [Root, C0_*, (end)]。
        /// 複数チェーン: 各 [C{c}_*, (end)]。multiChildType が First / Average なら chain 0 の先頭に Root を入れる。
        /// </summary>
        public static List<List<string>> AutoSprings(Rig rig, string multiChildType)
        {
            var result = new List<List<string>>();
            var rootSimulated = rig.chains.Count == 1 || multiChildType == "First" || multiChildType == "Average";
            for (var c = 0; c < rig.chains.Count; ++c)
            {
                var names = new List<string>();
                if (c == 0 && rootSimulated) names.Add(rig.Root.name);
                names.AddRange(rig.chains[c].Select(t => t.name));
                if (rig.endNodes[c] != null) names.Add(rig.endNodes[c].name);
                result.Add(names);
            }
            return result;
        }

        /// <summary>
        /// PhysBone コライダーの暫定変換 (補正なし)。
        /// Capsule は height を両端の半球込みの全長とみなし、ローカル Y 軸に沿わせる。
        /// </summary>
        public static VrmColliderSpec ConvertCollider(ColliderSpec pb)
        {
            var pos = HarnessMath.ToVector3(pb.position, Vector3.zero);
            var axis = Quaternion.Euler(HarnessMath.ToVector3(pb.rotationEuler, Vector3.zero)) * Vector3.up;
            var vrm = new VrmColliderSpec { attach = pb.attach, radius = pb.radius, offset = ToArray(pos) };
            switch (pb.shape)
            {
                case "Sphere":
                    vrm.type = pb.insideBounds ? "SphereInside" : "Sphere";
                    break;
                case "Capsule":
                    var half = Mathf.Max(0f, pb.height * 0.5f - pb.radius);
                    vrm.type = pb.insideBounds ? "CapsuleInside" : "Capsule";
                    vrm.offset = ToArray(pos - axis * half);
                    vrm.tail = ToArray(pos + axis * half);
                    break;
                case "Plane":
                    vrm.type = "Plane";
                    vrm.normal = ToArray(axis);
                    break;
                default:
                    throw new ArgumentException($"unknown collider shape: {pb.shape}");
            }
            return vrm;
        }

        static VRM10SpringBoneCollider AddCollider(Rig rig, VrmColliderSpec spec)
        {
            var col = rig.Find(spec.attach).gameObject.AddComponent<VRM10SpringBoneCollider>();
            if (!Enum.TryParse<VRM10SpringBoneColliderTypes>(spec.type, out col.ColliderType))
                throw new ArgumentException($"invalid VRM collider type: {spec.type}");
            col.Radius = spec.radius;
            col.Offset = HarnessMath.ToVector3(spec.offset, Vector3.zero);
            col.Tail = HarnessMath.ToVector3(spec.tail, Vector3.zero);
            col.Normal = HarnessMath.ToVector3(spec.normal, Vector3.up).normalized;
            return col;
        }

        static void ApplyJoint(VRM10SpringBoneJoint joint, VrmSpec spec, int spring, int index)
        {
            joint.m_stiffnessForce = PerJoint(spec, "stiffness", spring, index, spec.stiffness);
            joint.m_gravityPower = PerJoint(spec, "gravityPower", spring, index, spec.gravityPower);
            joint.m_gravityDir = HarnessMath.ToVector3(spec.gravityDir, Vector3.down).normalized;
            joint.m_dragForce = PerJoint(spec, "dragForce", spring, index, spec.dragForce);
            joint.m_jointRadius = PerJoint(spec, "hitRadius", spring, index, spec.hitRadius);

            if (!Enum.TryParse<AnglelimitTypes>(spec.limitType, out joint.m_anglelimitType))
                throw new ArgumentException($"invalid VRM limit type: {spec.limitType}");
            joint.m_limitSpaceOffset = Quaternion.Euler(HarnessMath.ToVector3(spec.limitOffsetEuler, Vector3.zero));
            switch (joint.m_anglelimitType)
            {
                case AnglelimitTypes.Cone:
                case AnglelimitTypes.Hinge:
                    joint.m_pitch = PerJoint(spec, "limitAngle", spring, index, spec.limitAngle) * Mathf.Deg2Rad;
                    joint.m_yaw = 0f;
                    break;
                case AnglelimitTypes.Spherical:
                    joint.m_pitch = PerJoint(spec, "pitch", spring, index, spec.pitch) * Mathf.Deg2Rad;
                    joint.m_yaw = PerJoint(spec, "yaw", spring, index, spec.yaw) * Mathf.Deg2Rad;
                    break;
            }
        }

        static float PerJoint(VrmSpec spec, string key, int spring, int index, float fallback)
        {
            if (spec.perSpring != null && spring < spec.perSpring.Count && spec.perSpring[spring] != null &&
                spec.perSpring[spring].TryGetValue(key, out var own) && own != null && own.Length > 0)
                return own[Mathf.Min(index, own.Length - 1)];
            if (spec.perJoint == null || !spec.perJoint.TryGetValue(key, out var values) || values == null || values.Length == 0)
                return fallback;
            return values[Mathf.Min(index, values.Length - 1)];
        }

        static BlittableColliderType ToBlittable(VRM10SpringBoneColliderTypes t)
        {
            switch (t)
            {
                case VRM10SpringBoneColliderTypes.Sphere: return BlittableColliderType.Sphere;
                case VRM10SpringBoneColliderTypes.Capsule: return BlittableColliderType.Capsule;
                case VRM10SpringBoneColliderTypes.Plane: return BlittableColliderType.Plane;
                case VRM10SpringBoneColliderTypes.SphereInside: return BlittableColliderType.SphereInside;
                case VRM10SpringBoneColliderTypes.CapsuleInside: return BlittableColliderType.CapsuleInside;
                default: throw new ArgumentOutOfRangeException(nameof(t));
            }
        }

        static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };
    }

    /// <summary>
    /// FastSpringBone のジョブを固定 dt で 1 ステップずつ回す。
    /// </summary>
    public sealed class VrmSpringSimulator : IDisposable
    {
        readonly FastSpringBoneBuffer _buffer;
        readonly FastSpringBoneScheduler _scheduler;

        public VrmSpringSimulator(Transform model, FastSpringBoneSpring[] springs)
        {
            var combiner = new FastSpringBoneBufferCombiner();
            _buffer = new FastSpringBoneBuffer(model, springs);
            combiner.Register(_buffer, null);
            _scheduler = new FastSpringBoneScheduler(combiner);
        }

        public void Step(float deltaTime)
        {
            _scheduler.Schedule(deltaTime).Complete();
        }

        public void Dispose()
        {
            // scheduler は combiner (結合バッファ) を破棄する。個別バッファは別途破棄が必要。
            _scheduler.Dispose();
            _buffer.Dispose();
        }
    }
}
