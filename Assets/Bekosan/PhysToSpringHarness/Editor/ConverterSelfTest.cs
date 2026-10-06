using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Bekosan.PhysToSpring.Editor;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using UniVRM10;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using Object = UnityEngine.Object;

namespace Bekosan.PhysToSpring.Harness.Editor
{
    /// <summary>
    /// 変換器の Writer まで通すセルフテスト (Edit Mode)。HarnessJobs/selftest.request を置くと実行し、
    /// HarnessJobs/results/selftest.json に結果を書く。テスト用のオブジェクトは最後に消す。
    /// </summary>
    static class ConverterSelfTest
    {
        public static void Run(string outPath)
        {
            var checks = new List<object>();
            void Check(string name, bool ok, string detail = "") => checks.Add(new { name, ok, detail });

            var root = new GameObject("P2S_SelfTest");
            try
            {
                var avatar = BuildAvatar(root.transform, out var hairPb, out var tailPb, out var col);

                var report = new ConversionReport();
                var vrm = PhysToSpringConverter.Convert(avatar, report);
                Check("convert ok", vrm != null, report.Summary());
                if (vrm != null)
                {
                    var springs = vrm.SpringBone.Springs;
                    Check("spring count", springs.Count == 3, string.Join(", ", springs.Select(s => $"{s.Name}[{string.Join(" ", s.Joints.Select(j => j.name))}]")));
                    var hair0 = springs.FirstOrDefault(s => s.Name == "HairRoot_0");
                    Check("first branch spring", hair0 != null && hair0.Joints.Select(j => j.name).SequenceEqual(new[] { "HairRoot", "HairA0", "HairA1", "HairA1" + SpringBoneWriter.EndSuffix }));
                    var hair1 = springs.FirstOrDefault(s => s.Name == "HairRoot_1");
                    Check("second branch spring", hair1 != null && hair1.Joints.Select(j => j.name).SequenceEqual(new[] { "HairB0", "HairB1", "HairB1" + SpringBoneWriter.EndSuffix }));
                    var tail = springs.FirstOrDefault(s => s.Name == "TailBase");
                    Check("ignore transform", tail != null && tail.Joints.Select(j => j.name).SequenceEqual(new[] { "Tail0", "Tail1", "Tail2" }),
                        tail == null ? "" : string.Join(" ", tail.Joints.Select(j => j.name)));
                    Check("center (immobile world)", tail != null && tail.Center == avatar && hair0 != null && hair0.Center == null);
                    Check("collider group", vrm.SpringBone.ColliderGroups.Count == 1 && hair0 != null && hair0.ColliderGroups.Count == 1 && tail != null && tail.ColliderGroups.Count == 0);
                    var vcol = col.GetComponent<VRM10SpringBoneCollider>();
                    Check("capsule collider", vcol != null && vcol.ColliderType == VRM10SpringBoneColliderTypes.Capsule &&
                                              (vcol.Offset - new Vector3(0, -0.05f, 0)).magnitude < 1e-5f && (vcol.Tail - new Vector3(0, 0.05f, 0)).magnitude < 1e-5f,
                        vcol == null ? "missing" : $"{vcol.ColliderType} {vcol.Offset} {vcol.Tail}");
                    var j0 = hair0?.Joints[0];
                    var expected = SpringMapper.Map(new PbJointParams { Pull = 0.3f, Spring = 0.4f, Gravity = 0.2f }, 0.05, true, 0, 0);
                    Check("joint params", j0 != null && Mathf.Abs(j0.m_stiffnessForce - expected.Stiffness) < 1e-4f && Mathf.Abs(j0.m_dragForce - expected.DragForce) < 1e-5f,
                        j0 == null ? "" : $"stiff {j0.m_stiffnessForce} vs {expected.Stiffness}, drag {j0.m_dragForce} vs {expected.DragForce}, grav {j0.m_gravityPower} vs {expected.GravityPower}");
                    var tailJ1 = tail?.Joints[1];
                    Check("curve (t=0.5 → pull x0.5)", tailJ1 != null &&
                        Mathf.Abs(tailJ1.m_stiffnessForce - SpringMapper.Map(new PbJointParams { Pull = 0.25f, Spring = 0.2f }, 0.05, false).Stiffness) < 1e-4f,
                        tailJ1 == null ? "" : $"{tailJ1.m_stiffnessForce}");

                    // 再変換しても増えない
                    var report2 = new ConversionReport();
                    var vrm2 = PhysToSpringConverter.Convert(avatar, report2);
                    var joints = avatar.GetComponentsInChildren<VRM10SpringBoneJoint>(true).Length;
                    var ends = avatar.GetComponentsInChildren<Transform>(true).Count(t => t.name.EndsWith(SpringBoneWriter.EndSuffix));
                    var cols = avatar.GetComponentsInChildren<VRM10SpringBoneCollider>(true).Length;
                    Check("reconvert idempotent", vrm2 == vrm && vrm2.SpringBone.Springs.Count == 3 && joints == 10 && ends == 2 && cols == 1,
                        $"springs {vrm2?.SpringBone.Springs.Count} joints {joints} ends {ends} colliders {cols}");

                    // immobile: AllMotion 1 → PB ルートの親が center、World 0.7 (閾値未満) → center なし
                    hairPb.immobileType = VRCPhysBoneBase.ImmobileType.AllMotion;
                    hairPb.immobile = 1f;
                    tailPb.immobile = 0.7f;
                    var vrm3 = PhysToSpringConverter.Convert(avatar, new ConversionReport());
                    var h = vrm3?.SpringBone.Springs.FirstOrDefault(s => s.Name == "HairRoot_0");
                    var tl = vrm3?.SpringBone.Springs.FirstOrDefault(s => s.Name == "TailBase");
                    Check("immobile center", h != null && h.Center != null && h.Center.name == "Head" && tl != null && tl.Center == null,
                        $"{h?.Center?.name} / {tl?.Center?.name}");
                    hairPb.immobile = 0f;
                    tailPb.immobile = 1f;
                }

                // エラー: 長さ 0 のボーン
                var zero = new GameObject("ZeroLen").transform;
                zero.SetParent(avatar, false);
                var z1 = Node("Z1", zero, Vector3.zero);
                var zpb = zero.gameObject.AddComponent<VRCPhysBone>();
                zpb.version = VRCPhysBoneBase.Version.Version_1_1;
                zpb.endpointPosition = new Vector3(0, -0.05f, 0);
                var zr = new ConversionReport();
                var before = avatar.GetComponentsInChildren<VRM10SpringBoneJoint>(true).Length;
                Check("zero length → error", PhysToSpringConverter.Convert(avatar, zr) == null && zr.HasError &&
                                             avatar.GetComponentsInChildren<VRM10SpringBoneJoint>(true).Length == before, zr.Summary());
                Object.DestroyImmediate(zero.gameObject);

                // エラー: Version 1.0
                hairPb.version = VRCPhysBoneBase.Version.Version_1_0;
                var vr = new ConversionReport();
                Check("version 1.0 → error", PhysToSpringConverter.Convert(avatar, vr) == null && vr.HasError, vr.Summary());
                hairPb.version = VRCPhysBoneBase.Version.Version_1_1;

                // メニューの後処理: VRM10Object 作成と PhysBone 削除
                var inst = avatar.GetComponent<Vrm10Instance>();
                if (inst != null)
                {
                    var pr = new ConversionReport();
                    PhysToSpringConverter.EnsureVrmObject(inst, pr);
                    var assetPath = inst.Vrm != null ? AssetDatabase.GetAssetPath(inst.Vrm) : "";
                    Check("vrm object created", inst.Vrm != null && !string.IsNullOrEmpty(assetPath), assetPath);
                    if (!string.IsNullOrEmpty(assetPath)) AssetDatabase.DeleteAsset(assetPath);
                }
                PhysToSpringConverter.RemovePhysBones(avatar, new ConversionReport());
                Check("physbones removed", avatar.GetComponentsInChildren<VRCPhysBone>(true).Length == 0 &&
                                           avatar.GetComponentsInChildren<VRCPhysBoneCollider>(true).Length == 0);
                var nr = new ConversionReport();
                var springsBefore = inst != null ? inst.SpringBone.Springs.Count : -1;
                Check("no physbone → error, springs kept", PhysToSpringConverter.Convert(avatar, nr) == null && nr.HasError &&
                                                           inst != null && inst.SpringBone.Springs.Count == springsBefore && springsBefore == 3, nr.Summary());
            }
            catch (Exception e)
            {
                Check("exception", false, e.ToString());
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            var json = JsonConvert.SerializeObject(new { ok = checks.All(c => (bool)c.GetType().GetProperty("ok").GetValue(c)), checks }, Formatting.Indented);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, json, new UTF8Encoding(false));
        }

        /// <summary>
        /// Avatar
        ///   Hips
        ///     Head (Capsule コライダー)
        ///       HairRoot (PB: First, endpoint) → HairA0 → HairA1 / HairB0 → HairB1
        ///     TailBase (PB: root Tail0, immobile World 1, pull カーブ, ignore Tail3) → Tail0 → Tail1 → Tail2 → Tail3
        /// </summary>
        static Transform BuildAvatar(Transform parent, out VRCPhysBone hairPb, out VRCPhysBone tailPb, out VRCPhysBoneCollider col)
        {
            var avatar = Node("Avatar", parent, Vector3.zero);
            var hips = Node("Hips", avatar, new Vector3(0, 1, 0));
            var head = Node("Head", hips, new Vector3(0, 0.5f, 0));
            col = head.gameObject.AddComponent<VRCPhysBoneCollider>();
            col.shapeType = VRCPhysBoneColliderBase.ShapeType.Capsule;
            col.radius = 0.05f;
            col.height = 0.2f;
            col.rotation = Quaternion.identity;

            // 下へ伸びる髪 (α = 0)。First の分岐なので HairRoot も最初の子へ向けて動く
            var hairRoot = Node("HairRoot", head, new Vector3(0, 0, -0.05f));
            var a0 = Node("HairA0", hairRoot, new Vector3(0, -0.05f, 0));
            Node("HairA1", a0, new Vector3(0, -0.05f, 0));
            var b0 = Node("HairB0", hairRoot, new Vector3(0.02f, -0.05f, 0));
            Node("HairB1", b0, new Vector3(0, -0.05f, 0));
            hairPb = hairRoot.gameObject.AddComponent<VRCPhysBone>();
            hairPb.version = VRCPhysBoneBase.Version.Version_1_1;
            hairPb.multiChildType = VRCPhysBoneBase.MultiChildType.First;
            hairPb.endpointPosition = new Vector3(0, -0.05f, 0);
            hairPb.pull = 0.3f;
            hairPb.spring = 0.4f;
            hairPb.gravity = 0.2f;
            hairPb.colliders = new List<VRCPhysBoneColliderBase> { col };

            var tailBase = Node("TailBase", hips, new Vector3(0, 0, -0.1f));
            var t0 = Node("Tail0", tailBase, new Vector3(0, -0.05f, 0));
            var t1 = Node("Tail1", t0, new Vector3(0, -0.05f, 0));
            var t2 = Node("Tail2", t1, new Vector3(0, -0.05f, 0));
            var t3 = Node("Tail3", t2, new Vector3(0, -0.05f, 0));
            tailPb = tailBase.gameObject.AddComponent<VRCPhysBone>();
            tailPb.version = VRCPhysBoneBase.Version.Version_1_1;
            tailPb.rootTransform = t0;
            tailPb.ignoreTransforms = new List<Transform> { t3 };
            tailPb.pull = 0.5f;
            tailPb.pullCurve = AnimationCurve.Linear(0, 1, 1, 0);
            tailPb.spring = 0.2f;
            tailPb.immobileType = VRCPhysBoneBase.ImmobileType.World;
            tailPb.immobile = 1f;
            tailPb.allowCollision = VRCPhysBoneBase.AdvancedBool.False;
            tailPb.colliders = new List<VRCPhysBoneColliderBase> { col };
            return avatar;
        }

        static Transform Node(string name, Transform parent, Vector3 localPosition)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = localPosition;
            return t;
        }
    }
}
