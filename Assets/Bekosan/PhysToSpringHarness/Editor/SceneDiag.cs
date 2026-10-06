using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using UniVRM10;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Bekosan.PhysToSpring.Harness.Editor
{
    /// <summary>
    /// 開いているシーンの PhysBone と Vrm10Instance の設定を JSON に書き出す (変換結果の調査用)。
    /// HarnessJobs/diag.request を置くと実行し、HarnessJobs/results/diag.json に書く。
    /// </summary>
    static class SceneDiag
    {
        public static void Run(string outPath)
        {
            var avatars = new List<object>();
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;
                foreach (var go in scene.GetRootGameObjects())
                {
                    var pbs = go.GetComponentsInChildren<VRCPhysBone>(true);
                    var insts = go.GetComponentsInChildren<Vrm10Instance>(true);
                    if (pbs.Length == 0 && insts.Length == 0) continue;
                    avatars.Add(new
                    {
                        scene = scene.name,
                        root = go.name,
                        active = go.activeInHierarchy,
                        physBones = pbs.Select(pb => DumpPb(pb, go.transform)).ToList(),
                        vrm = insts.Select(x => DumpVrm(x, go.transform)).ToList(),
                    });
                }
            }
            var json = JsonConvert.SerializeObject(new { avatars }, Formatting.Indented);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, json, new UTF8Encoding(false));
        }

        static object DumpPb(VRCPhysBone pb, Transform root)
        {
            string Curve(AnimationCurve c) => c == null || c.length == 0 ? null : string.Join(" ", c.keys.Select(k => $"{k.time:0.##}:{k.value:0.###}"));
            return new
            {
                path = PathOf(pb.transform, root),
                rootTransform = pb.rootTransform != null ? PathOf(pb.rootTransform, root) : null,
                enabled = pb.enabled && pb.gameObject.activeInHierarchy,
                version = pb.version.ToString(),
                integration = pb.integrationType.ToString(),
                pb.pull, pullCurve = Curve(pb.pullCurve),
                pb.spring, springCurve = Curve(pb.springCurve),
                pb.stiffness, stiffnessCurve = Curve(pb.stiffnessCurve),
                pb.gravity, gravityCurve = Curve(pb.gravityCurve),
                pb.gravityFalloff,
                immobileType = pb.immobileType.ToString(), pb.immobile, immobileCurve = Curve(pb.immobileCurve),
                limitType = pb.limitType.ToString(), pb.maxAngleX, pb.maxAngleZ,
                multiChild = pb.multiChildType.ToString(),
                endpoint = V(pb.endpointPosition),
                pb.radius,
                ignore = pb.ignoreTransforms?.Count ?? 0,
                colliders = pb.colliders?.Count ?? 0,
                lossyScale = V((pb.rootTransform != null ? pb.rootTransform : pb.transform).lossyScale),
            };
        }

        static object DumpVrm(Vrm10Instance inst, Transform root)
        {
            return new
            {
                path = PathOf(inst.transform, root),
                inst.enabled,
                active = inst.gameObject.activeInHierarchy,
                vrmObject = inst.Vrm != null ? inst.Vrm.name : null,
                updateType = inst.UpdateType.ToString(),
                springs = inst.SpringBone.Springs.Select(s => new
                {
                    s.Name,
                    center = s.Center != null ? PathOf(s.Center, root) : null,
                    colliderGroups = s.ColliderGroups.Count,
                    joints = s.Joints.Select(j => j == null ? null : new
                    {
                        name = j.name,
                        stiffness = j.m_stiffnessForce,
                        drag = j.m_dragForce,
                        gravity = j.m_gravityPower,
                        radius = j.m_jointRadius,
                        limit = j.m_anglelimitType.ToString(),
                        pitch = j.m_pitch * Mathf.Rad2Deg,
                        yaw = j.m_yaw * Mathf.Rad2Deg,
                        len = j.transform.parent != null ? j.transform.localPosition.magnitude * j.transform.parent.lossyScale.x : 0f,
                    }).ToList(),
                }).ToList(),
            };
        }

        static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };

        static string PathOf(Transform t, Transform root)
        {
            var names = new List<string>();
            for (var x = t; x != null && x != root.parent; x = x.parent) names.Add(x.name);
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
