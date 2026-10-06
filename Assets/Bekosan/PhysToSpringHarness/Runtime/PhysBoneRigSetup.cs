using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Bekosan.PhysToSpring.Harness
{
    /// <summary>
    /// 計測用リグに VRCPhysBone / VRCPhysBoneCollider を付ける。
    /// PhysBone の内部実装には触れず、公開フィールドの設定だけを行う。
    /// リグは非アクティブの状態で呼び出すこと (設定完了後に有効化して初期化させる)。
    /// </summary>
    public static class PhysBoneRigSetup
    {
        public static VRCPhysBone Setup(Rig rig, RigSpec rigSpec, PhysBoneSpec spec, List<ColliderSpec> colliders)
        {
            if (rigSpec.avatarDescriptorOnDriver)
            {
                var type = Type.GetType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor, VRCSDK3A")
                           ?? throw new InvalidOperationException("VRCAvatarDescriptor not found");
                rig.Driver.gameObject.AddComponent(type);
            }
            var pb = rig.Root.gameObject.AddComponent<VRCPhysBone>();
            pb.version = VRCPhysBoneBase.Version.Version_1_1;
            pb.rootTransform = rig.Root;
            if (pb.ignoreTransforms == null) pb.ignoreTransforms = new List<Transform>();
            pb.endpointPosition = rigSpec.endpoint != null ? HarnessMath.ToVector3(rigSpec.endpoint, Vector3.zero) : Vector3.zero;

            pb.integrationType = ParseEnum<VRCPhysBoneBase.IntegrationType>(spec.integrationType);
            pb.multiChildType = ParseEnum<VRCPhysBoneBase.MultiChildType>(spec.multiChildType);
            pb.pull = spec.pull;
            pb.spring = spec.spring;
            pb.stiffness = spec.stiffness;
            pb.gravity = spec.gravity;
            pb.gravityFalloff = spec.gravityFalloff;
            pb.immobileType = ParseEnum<VRCPhysBoneBase.ImmobileType>(spec.immobileType);
            pb.immobile = spec.immobile;
            pb.radius = spec.radius;
            pb.limitType = ParseEnum<VRCPhysBoneBase.LimitType>(spec.limitType);
            pb.maxAngleX = spec.maxAngleX;
            pb.maxAngleZ = spec.maxAngleZ;
            pb.limitRotation = HarnessMath.ToVector3(spec.limitRotation, Vector3.zero);

            // 計測対象外の機能は無効化する
            pb.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
            pb.allowGrabbing = VRCPhysBoneBase.AdvancedBool.False;
            pb.allowPosing = VRCPhysBoneBase.AdvancedBool.False;
            pb.maxStretch = 0f;
            pb.maxSquish = 0f;
            pb.stretchMotion = 0f;
            pb.isAnimated = false;
            pb.parameter = "";

            foreach (var c in spec.curves)
            {
                var curve = LinearCurve(c.keys);
                switch (c.param)
                {
                    case "pull": pb.pullCurve = curve; break;
                    case "spring": pb.springCurve = curve; break;
                    case "stiffness": pb.stiffnessCurve = curve; break;
                    case "gravity": pb.gravityCurve = curve; break;
                    case "gravityFalloff": pb.gravityFalloffCurve = curve; break;
                    case "immobile": pb.immobileCurve = curve; break;
                    case "radius": pb.radiusCurve = curve; break;
                    case "maxAngleX": pb.maxAngleXCurve = curve; break;
                    case "maxAngleZ": pb.maxAngleZCurve = curve; break;
                    default: throw new ArgumentException($"unknown curve param: {c.param}");
                }
            }

            pb.colliders = new List<VRCPhysBoneColliderBase>();
            for (var i = 0; i < colliders.Count; ++i)
            {
                pb.colliders.Add(AddCollider(rig, colliders[i]));
            }
            return pb;
        }

        static VRCPhysBoneCollider AddCollider(Rig rig, ColliderSpec spec)
        {
            var attach = rig.Find(spec.attach);
            var col = attach.gameObject.AddComponent<VRCPhysBoneCollider>();
            col.rootTransform = attach;
            col.shapeType = ParseEnum<VRCPhysBoneColliderBase.ShapeType>(spec.shape);
            col.radius = spec.radius;
            col.height = spec.height;
            col.position = HarnessMath.ToVector3(spec.position, Vector3.zero);
            col.rotation = Quaternion.Euler(HarnessMath.ToVector3(spec.rotationEuler, Vector3.zero));
            col.insideBounds = spec.insideBounds;
            col.bonesAsSpheres = spec.bonesAsSpheres;
            return col;
        }

        /// <summary>[t0, v0, t1, v1, ...] から折れ線のカーブを作る。</summary>
        static AnimationCurve LinearCurve(float[] keys)
        {
            if (keys == null || keys.Length < 2 || keys.Length % 2 != 0)
                throw new ArgumentException("curve keys must be [t0, v0, t1, v1, ...]");
            var n = keys.Length / 2;
            var frames = new Keyframe[n];
            for (var i = 0; i < n; ++i)
            {
                var t = keys[i * 2];
                var v = keys[i * 2 + 1];
                var inTan = i > 0 ? Slope(keys, i - 1, i) : 0f;
                var outTan = i < n - 1 ? Slope(keys, i, i + 1) : 0f;
                frames[i] = new Keyframe(t, v, inTan, outTan);
            }
            return new AnimationCurve(frames);
        }

        static float Slope(float[] keys, int a, int b)
        {
            var dt = keys[b * 2] - keys[a * 2];
            return Mathf.Abs(dt) < 1e-9f ? 0f : (keys[b * 2 + 1] - keys[a * 2 + 1]) / dt;
        }

        static T ParseEnum<T>(string s) where T : struct, Enum
        {
            if (Enum.TryParse<T>(s, out var v)) return v;
            throw new ArgumentException($"invalid {typeof(T).Name}: {s}");
        }
    }
}
