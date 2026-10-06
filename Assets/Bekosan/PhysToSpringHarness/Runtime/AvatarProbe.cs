using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Bekosan.PhysToSpring.Harness
{
    /// <summary>
    /// 実アバターのコピーを Driver の下で動かし、spring ごとのボーンの振れ角を記録する (Play Mode)。
    /// 各フレームの Update 先頭で前フレームの結果 (PhysBone は LateUpdate 後に書き戻される) を記録してから Driver を動かす。
    /// 振れ角は各セグメントの向きを親ボーンのローカル空間で見て、warmup 終了時の向きからの角度。
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    public sealed class AvatarProbe : MonoBehaviour
    {
        public sealed class Copy
        {
            public string motion;
            public string variant;
            public Transform driver;
            public Transform avatar;
        }

        public sealed class Motion
        {
            public string name;
            /// <summary>sinePos | sineRot | rampPos</summary>
            public string type;
            public Vector3 axis;
            public float amplitude;
            public float frequency;
        }

        public float fps = 60f;
        public float warmup = 1f;
        public float duration = 4f;
        public string outPath;
        public readonly List<Copy> copies = new List<Copy>();
        public readonly List<Motion> motions = new List<Motion>();
        /// <summary>spring 名 → joint のパス (アバタールートからの相対)。</summary>
        public readonly List<(string name, List<string> paths)> springs = new List<(string, List<string>)>();

        public static bool Done;

        sealed class Seg
        {
            public Transform bone, child;
            public Vector3 rest;
            public double sumSq;
            public float max;
            public int n;
        }

        readonly Dictionary<Copy, List<List<Seg>>> _segs = new Dictionary<Copy, List<List<Seg>>>();
        int _frame;
        int _warmupFrames, _totalFrames;

        void Start()
        {
            Done = false;
            Time.captureDeltaTime = 1f / fps;
            _warmupFrames = Mathf.RoundToInt(warmup * fps);
            _totalFrames = _warmupFrames + Mathf.RoundToInt(duration * fps);
            foreach (var c in copies)
            {
                var list = new List<List<Seg>>();
                foreach (var (_, paths) in springs)
                {
                    var bones = paths.Select(p => string.IsNullOrEmpty(p) ? c.avatar : c.avatar.Find(p)).ToList();
                    var segs = new List<Seg>();
                    for (var i = 0; i + 1 < bones.Count; ++i)
                    {
                        if (bones[i] == null || bones[i + 1] == null || bones[i].parent == null) continue;
                        segs.Add(new Seg { bone = bones[i], child = bones[i + 1] });
                    }
                    list.Add(segs);
                }
                _segs[c] = list;
            }
        }

        void OnDestroy() => Time.captureDeltaTime = 0f;

        static Vector3 Dir(Seg s) => s.bone.parent.InverseTransformDirection(s.child.position - s.bone.position).normalized;

        void Update()
        {
            if (Done) return;
            // 前フレームの結果を記録
            if (_frame == _warmupFrames)
            {
                foreach (var s in _segs.Values.SelectMany(x => x).SelectMany(x => x)) s.rest = Dir(s);
            }
            else if (_frame > _warmupFrames)
            {
                foreach (var s in _segs.Values.SelectMany(x => x).SelectMany(x => x))
                {
                    var a = Vector3.Angle(s.rest, Dir(s));
                    s.sumSq += a * a;
                    s.n++;
                    if (a > s.max) s.max = a;
                }
            }
            if (_frame >= _totalFrames)
            {
                Write();
                Done = true;
                return;
            }

            var t = (_frame - _warmupFrames) / fps;
            foreach (var c in copies)
            {
                var m = motions.First(x => x.name == c.motion);
                Evaluate(m, Mathf.Max(0f, t), out var pos, out var rot);
                c.driver.localPosition = pos;
                c.driver.localRotation = rot;
            }
            _frame++;
        }

        static void Evaluate(Motion m, float t, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var s = Mathf.Sin(2f * Mathf.PI * m.frequency * t);
            switch (m.type)
            {
                case "sinePos": pos = m.axis * (m.amplitude * s); break;
                case "sineRot": rot = Quaternion.AngleAxis(m.amplitude * s, m.axis); break;
                case "rampPos":
                    // 1/frequency 秒で amplitude だけ進んで止まる (歩き出しと停止)
                    var u = Mathf.Clamp01(t * m.frequency);
                    pos = m.axis * (m.amplitude * u * u * (3f - 2f * u));
                    break;
                default: throw new ArgumentException("motion type: " + m.type);
            }
        }

        void Write()
        {
            var result = motions.Select(m => new
            {
                motion = m.name,
                springs = springs.Select((sp, i) => new
                {
                    sp.name,
                    variants = copies.Where(c => c.motion == m.name).ToDictionary(c => c.variant, c =>
                    {
                        var segs = _segs[c][i];
                        return new
                        {
                            segs = segs.Count,
                            rms = segs.Count == 0 ? 0 : segs.Average(s => s.n == 0 ? 0 : Math.Sqrt(s.sumSq / s.n)),
                            max = segs.Count == 0 ? 0 : segs.Max(s => s.max),
                        };
                    }),
                }).ToList(),
            }).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, JsonConvert.SerializeObject(result, Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
