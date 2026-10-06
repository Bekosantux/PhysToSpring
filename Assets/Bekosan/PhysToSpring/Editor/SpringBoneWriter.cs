using System.Collections.Generic;
using System.Linq;
using UniGLTF.SpringBoneJobs;
using UnityEditor;
using UnityEngine;
using UniVRM10;

namespace Bekosan.PhysToSpring.Editor
{
    /// <summary>
    /// SpringPlan を Vrm10Instance の SpringBone (joint / collider / collider group) として書き込む。Undo 対応。
    /// </summary>
    public static class SpringBoneWriter
    {
        /// <summary>PhysBone の endpoint 用に作るノードの接尾辞。再変換時の掃除にも使う。</summary>
        public const string EndSuffix = "_p2s_end";

        public static Vrm10Instance Write(Transform avatarRoot, List<SpringPlan> plans, ConversionReport report)
        {
            var instance = avatarRoot.GetComponent<Vrm10Instance>();
            if (instance == null)
            {
                // RequireComponent で Humanoid も付き、Animator からボーンが割り当てられる
                instance = Undo.AddComponent<Vrm10Instance>(avatarRoot.gameObject);
                report.Info("Vrm10Instance を追加しました。VRM メタ情報 (VRM10Object) は別途設定してください", instance);
            }
            Clear(avatarRoot, instance);

            Undo.RecordObject(instance, "PhysToSpring");
            var groups = new Dictionary<ColliderPlan, VRM10SpringBoneColliderGroup>();
            foreach (var plan in plans)
            {
                var spring = new Vrm10InstanceSpringBone.Spring(plan.Name)
                {
                    Center = plan.Center,
                };
                foreach (var colliderPlan in plan.Colliders)
                {
                    if (!groups.TryGetValue(colliderPlan, out var group))
                    {
                        group = AddCollider(colliderPlan);
                        groups[colliderPlan] = group;
                        instance.SpringBone.ColliderGroups.Add(group);
                    }
                    spring.ColliderGroups.Add(group);
                }

                var nodes = new List<Transform>(plan.Bones);
                if (plan.EndpointLocal.HasValue)
                {
                    var leaf = plan.Bones[plan.Bones.Count - 1];
                    var end = new GameObject(leaf.name + EndSuffix).transform;
                    Undo.RegisterCreatedObjectUndo(end.gameObject, "PhysToSpring");
                    end.SetParent(leaf, false);
                    end.localPosition = plan.EndpointLocal.Value;
                    nodes.Add(end);
                }
                for (var i = 0; i < nodes.Count; ++i)
                {
                    var joint = Undo.AddComponent<VRM10SpringBoneJoint>(nodes[i].gameObject);
                    // tail の値は使われないが、最後のセグメントと揃えておく
                    Apply(joint, plan.Joints[Mathf.Min(i, plan.Joints.Count - 1)], i < plan.Joints.Count);
                    spring.Joints.Add(joint);
                }
                instance.SpringBone.Springs.Add(spring);
            }
            EditorUtility.SetDirty(instance);
            return instance;
        }

        /// <summary>既存の SpringBone 設定と、前回の変換で作った end ノードを消す。</summary>
        static void Clear(Transform avatarRoot, Vrm10Instance instance)
        {
            Undo.RecordObject(instance, "PhysToSpring");
            instance.SpringBone.Springs.Clear();
            instance.SpringBone.ColliderGroups.Clear();
            foreach (var c in avatarRoot.GetComponentsInChildren<VRM10SpringBoneJoint>(true)) Undo.DestroyObjectImmediate(c);
            foreach (var c in avatarRoot.GetComponentsInChildren<VRM10SpringBoneColliderGroup>(true)) Undo.DestroyObjectImmediate(c);
            foreach (var c in avatarRoot.GetComponentsInChildren<VRM10SpringBoneCollider>(true)) Undo.DestroyObjectImmediate(c);
            foreach (var t in avatarRoot.GetComponentsInChildren<Transform>(true).Where(IsGeneratedEnd).ToList())
            {
                Undo.DestroyObjectImmediate(t.gameObject);
            }
        }

        public static bool IsGeneratedEnd(Transform t)
        {
            return t.name.EndsWith(EndSuffix) && t.childCount == 0 &&
                   t.GetComponents<Component>().All(c => c is Transform || c is VRM10SpringBoneJoint);
        }

        static void Apply(VRM10SpringBoneJoint joint, JointPlan plan, bool limit)
        {
            joint.m_stiffnessForce = plan.Params.Stiffness;
            joint.m_gravityPower = plan.Params.GravityPower;
            joint.m_gravityDir = Vector3.down;
            joint.m_dragForce = plan.Params.DragForce;
            joint.m_jointRadius = plan.HitRadius;
            joint.m_anglelimitType = limit ? plan.LimitType : AnglelimitTypes.None;
            joint.m_limitSpaceOffset = plan.LimitOffset;
            joint.m_pitch = plan.Pitch;
            joint.m_yaw = plan.Yaw;
        }

        /// <summary>ColliderPlan 1 個を VRM コライダー + それだけを持つグループにする。</summary>
        static VRM10SpringBoneColliderGroup AddCollider(ColliderPlan plan)
        {
            var go = plan.Attach.gameObject;
            var col = Undo.AddComponent<VRM10SpringBoneCollider>(go);
            col.ColliderType = plan.Type;
            col.Radius = plan.Radius;
            col.Offset = plan.Offset;
            if (plan.Type == VRM10SpringBoneColliderTypes.Capsule || plan.Type == VRM10SpringBoneColliderTypes.CapsuleInside) col.Tail = plan.Tail;
            if (plan.Type == VRM10SpringBoneColliderTypes.Plane) col.Normal = plan.Normal;
            var group = Undo.AddComponent<VRM10SpringBoneColliderGroup>(go);
            group.Name = plan.Name;
            group.Colliders.Add(col);
            return group;
        }
    }
}
