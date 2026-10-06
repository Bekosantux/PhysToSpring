using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UniVRM10;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Bekosan.PhysToSpring.Harness.Editor
{
    /// <summary>
    /// 開いているシーンの変換済みアバター "{名前} (VRM)" と元アバター "{名前}" を Play Mode で並べて動かし、
    /// spring ごとの振れ角を比べる。HarnessJobs/avatarprobe.request を置くと実行し、results/avatarprobe.json に書く。
    /// variant: pb (元のまま) / pb_imm0 (全 PhysBone の immobile = 0) / vrm (変換結果)。
    /// Play Mode 中の変更なのでシーンには残らない。
    /// </summary>
    [InitializeOnLoad]
    static class AvatarProbeLauncher
    {
        const string Owned = "P2S_AvatarProbe_Owned";
        const string Pending = "P2S_AvatarProbe_Pending";

        static AvatarProbeLauncher()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnUpdate;
        }

        /// <summary>HarnessLauncher から Edit Mode で呼ぶ。</summary>
        public static void Start()
        {
            SessionState.SetBool(Pending, true);
            SessionState.SetBool(Owned, true);
            EditorApplication.isPlaying = true;
        }

        static void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Pending, false)) return;
            SessionState.SetBool(Pending, false);
            try
            {
                Setup();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
                File.WriteAllText(OutPath, "{\"error\": " + Newtonsoft.Json.JsonConvert.SerializeObject(e.ToString()) + "}");
                AvatarProbe.Done = true;
            }
        }

        static void OnUpdate()
        {
            if (!SessionState.GetBool(Owned, false) || !EditorApplication.isPlaying || !AvatarProbe.Done) return;
            SessionState.SetBool(Owned, false);
            AvatarProbe.Done = false;
            Debug.Log("[PhysToSpringHarness] avatarprobe done");
            EditorApplication.isPlaying = false;
        }

        static string OutPath => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HarnessJobs", "results", "avatarprobe.json"));

        static void Setup()
        {
            AvatarProbe.Done = false;
            var vrm = Object.FindObjectsOfType<Vrm10Instance>(true).FirstOrDefault(x => x.gameObject.scene.IsValid() && x.name.EndsWith(" (VRM)"))
                      ?? throw new System.InvalidOperationException("no '* (VRM)' avatar in scene");
            var srcName = vrm.name.Substring(0, vrm.name.Length - " (VRM)".Length);
            var src = Object.FindObjectsOfType<VRCPhysBone>(true)
                          .Select(pb => pb.transform)
                          .Select(t => { while (t != null && t.name != srcName) t = t.parent; return t; })
                          .FirstOrDefault(t => t != null)
                      ?? throw new System.InvalidOperationException("source avatar not found: " + srcName);

            var probeGo = new GameObject("AvatarProbe");
            probeGo.transform.position = new Vector3(100f, 0f, 0f);
            var probe = probeGo.AddComponent<AvatarProbe>();
            probe.outPath = OutPath;
            foreach (var s in vrm.SpringBone.Springs)
            {
                probe.springs.Add((s.Name, s.Joints.Where(j => j != null)
                    .Select(j => AnimationUtility.CalculateTransformPath(j.transform, vrm.transform)).ToList()));
            }
            probe.motions.Add(new AvatarProbe.Motion { name = "pos", type = "sinePos", axis = Vector3.right, amplitude = 0.3f, frequency = 1f });
            probe.motions.Add(new AvatarProbe.Motion { name = "turn", type = "sineRot", axis = Vector3.up, amplitude = 45f, frequency = 0.5f });
            probe.motions.Add(new AvatarProbe.Motion { name = "walk", type = "rampPos", axis = Vector3.forward, amplitude = 1.5f, frequency = 1f });

            // シーン上の変換済みアバターは Play Mode の間だけ隠す (コピーと二重に表示しない)
            vrm.gameObject.SetActive(false);
            var variants = new (string name, Transform source)[] { ("pb", src), ("pb_imm0", src), ("vrm", vrm.transform) };
            for (var mi = 0; mi < probe.motions.Count; ++mi)
            {
                for (var vi = 0; vi < variants.Length; ++vi)
                {
                    var driver = new GameObject($"Driver_{probe.motions[mi].name}_{variants[vi].name}").transform;
                    driver.SetParent(probeGo.transform, false);
                    var anchor = new GameObject("Anchor").transform;
                    anchor.SetParent(probeGo.transform, false);
                    anchor.localPosition = new Vector3(2f * vi, 0f, 4f * mi);
                    driver.SetParent(anchor, false);

                    var wasActive = variants[vi].source.gameObject.activeSelf;
                    variants[vi].source.gameObject.SetActive(false);
                    var copy = Object.Instantiate(variants[vi].source.gameObject, driver, false).transform;
                    variants[vi].source.gameObject.SetActive(wasActive);
                    copy.localPosition = Vector3.zero;
                    copy.localRotation = Quaternion.identity;
                    if (variants[vi].name == "pb_imm0")
                    {
                        foreach (var pb in copy.GetComponentsInChildren<VRCPhysBone>(true)) pb.immobile = 0f;
                    }
                    foreach (var a in copy.GetComponentsInChildren<Animator>(true)) a.enabled = false;
                    copy.gameObject.SetActive(true);
                    probe.copies.Add(new AvatarProbe.Copy { motion = probe.motions[mi].name, variant = variants[vi].name, driver = driver, avatar = copy });
                }
            }
        }
    }
}
