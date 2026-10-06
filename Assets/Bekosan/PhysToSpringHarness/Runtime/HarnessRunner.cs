using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using VRC.Dynamics;

namespace Bekosan.PhysToSpring.Harness
{
    public sealed class JobCaseResult
    {
        public string name;
        public float meanErrRmsDeg;
        public float maxErrDeg;
        public float meanErrRmsWarmupDeg;
        public float meanErrRmsMotionDeg;
        public float meanErrFinalDeg;
        public float tipErrRms;
        public List<string> warnings;
    }

    public sealed class JobSummary
    {
        public string name;
        public float fps;
        public float meanErrRmsDeg;
        public float meanTipErrRms;
        public List<JobCaseResult> cases = new List<JobCaseResult>();
    }

    /// <summary>
    /// Play Mode 中に HarnessJob を 1 ケースずつ実行する。
    ///
    /// 1 フレームの流れ (dt = 1/fps 固定):
    ///   Update 後 (coroutine)        : Driver に motion の姿勢を設定 (両リグ同一)
    ///   LateUpdate (order 0)         : PhysBoneManager がシミュレーション (ブラックボックス)
    ///   LateUpdate (order 32000)     : PhysBone のジョブ完了を待つ → VRM を 1 ステップ → 記録
    ///   次フレーム Update 後          : pbSampleTiming = nextFrame なら、Driver 更新前に PhysBone を記録
    /// WaitForEndOfFrame はバッチモードや Game ビュー非表示時に来ないことがあるので使わない。
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public sealed class HarnessRunner : MonoBehaviour
    {
        Action _lateAction;
        string _lateError;

        void LateUpdate()
        {
            var action = _lateAction;
            _lateAction = null;
            if (action == null) return;
            Guard(action, ref _lateError);
        }

        HarnessJob _job;
        string _outDir;
        Action<bool, string> _onFinished;
        float _prevCaptureDeltaTime;
        float _prevFixedDeltaTime;
        bool _prevRunInBackground;
        int _prevTargetFrameRate;
        int _prevVSyncCount;

        /// <summary>
        /// VrmSpec.fromConverter 用。(PhysBone リグの Base, 元の spec) から変換器の結果を入れた spec を返す。
        /// 変換器はエディタアセンブリなので、ハーネスのエディタ側が登録する。
        /// </summary>
        public static Func<Transform, VrmSpec, VrmSpec> ConverterSpecProvider;

        public static HarnessRunner Launch(HarnessJob job, string outDir, Action<bool, string> onFinished)
        {
            var go = new GameObject("PhysToSpringHarness");
            var runner = go.AddComponent<HarnessRunner>();
            runner._job = job;
            runner._outDir = outDir;
            runner._onFinished = onFinished;
            runner.StartCoroutine(runner.Run());
            return runner;
        }

        IEnumerator Run()
        {
            _prevCaptureDeltaTime = Time.captureDeltaTime;
            _prevFixedDeltaTime = Time.fixedDeltaTime;
            _prevRunInBackground = Application.runInBackground;
            _prevTargetFrameRate = Application.targetFrameRate;
            _prevVSyncCount = QualitySettings.vSyncCount;
            var dt = 1f / _job.fps;
            Time.captureDeltaTime = dt;
            Time.fixedDeltaTime = dt;
            Application.runInBackground = true;
            // captureDeltaTime で時間は固定されるので、実時間側は待たずに回す
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            // 設定を反映させるために 1 フレーム待つ
            yield return null;

            var summary = new JobSummary { name = _job.name, fps = _job.fps };
            string error = null;
            if (!Guard(() =>
                {
                    if (PhysBoneManager.Inst == null) throw new InvalidOperationException("PhysBoneManager is not initialized");
                    Directory.CreateDirectory(_outDir);
                    WriteJson(Path.Combine(_outDir, "job.json"), _job);
                }, ref error))
            {
                Finish(false, error);
                yield break;
            }

            // parallelCases なら全ケースを同時に、そうでなければ 1 ケースずつ回す
            var batches = _job.parallelCases
                ? new List<List<HarnessCase>> { _job.cases }
                : _job.cases.Select(x => new List<HarnessCase> { x }).ToList();
            foreach (var batch in batches)
            {
                var runs = new List<CaseRun>();
                for (var i = 0; i < batch.Count; ++i)
                {
                    var c = batch[i];
                    var offset = new Vector3(0f, 0f, _job.rigSpacing * i);
                    if (!Guard(() => runs.Add(new CaseRun(c, _job, transform, offset)), ref error))
                    {
                        runs.ForEach(r => r.Dispose());
                        Finish(false, $"case {c.name}: {error}");
                        yield break;
                    }
                }
                var frameCount = runs.Max(r => r.frameCount);

                // f フレーム目: この coroutine (Update 後) で姿勢を設定し、同じフレームの LateUpdate で記録する
                for (var f = 0; f <= frameCount; ++f)
                {
                    yield return null;
                    if (_lateError != null)
                    {
                        runs.ForEach(r => r.Dispose());
                        Finish(false, $"frame {f - 1}: {_lateError}");
                        yield break;
                    }
                    if (f > 0)
                    {
                        // 前フレームの PhysBone 結果を、Driver を更新する前に記録する
                        var prev = f - 1;
                        if (!Guard(() =>
                            {
                                foreach (var r in runs)
                                    if (prev < r.frameCount && !r.pbSampleInLateUpdate) r.RecordPhysBone(prev);
                            }, ref error))
                        {
                            runs.ForEach(r => r.Dispose());
                            Finish(false, $"frame {prev}: {error}");
                            yield break;
                        }
                    }
                    if (f == frameCount) break;
                    var frame = f;
                    var active = runs.Where(r => frame < r.frameCount).ToList();
                    if (!Guard(() => active.ForEach(r => r.BeforeSimulation(frame)), ref error))
                    {
                        runs.ForEach(r => r.Dispose());
                        Finish(false, $"frame {frame}: {error}");
                        yield break;
                    }
                    _lateAction = () =>
                    {
                        PhysBoneManager.Inst.CompleteJob();
                        foreach (var r in active) r.AfterSimulation(frame, Time.deltaTime);
                    };
                }

                for (var i = 0; i < runs.Count; ++i)
                {
                    var run = runs[i];
                    var c = batch[i];
                    if (!Guard(() => summary.cases.Add(run.Save(Path.Combine(_outDir, SafeName(c.name)))), ref error))
                    {
                        runs.ForEach(r => r.Dispose());
                        Finish(false, $"case {c.name}: {error}");
                        yield break;
                    }
                }
                runs.ForEach(r => r.Dispose());
                // 破棄を反映させてから次のケースへ
                yield return null;
            }

            if (summary.cases.Count > 0)
            {
                summary.meanErrRmsDeg = summary.cases.Average(x => x.meanErrRmsDeg);
                summary.meanTipErrRms = summary.cases.Average(x => x.tipErrRms);
            }
            if (!Guard(() => WriteJson(Path.Combine(_outDir, "summary.json"), summary), ref error))
            {
                Finish(false, error);
                yield break;
            }
            Finish(true, $"{summary.cases.Count} cases, meanErrRms={summary.meanErrRmsDeg:F3}deg");
        }

        void Finish(bool ok, string message)
        {
            Time.captureDeltaTime = _prevCaptureDeltaTime;
            Time.fixedDeltaTime = _prevFixedDeltaTime;
            Application.runInBackground = _prevRunInBackground;
            Application.targetFrameRate = _prevTargetFrameRate;
            QualitySettings.vSyncCount = _prevVSyncCount;
            if (ok) Debug.Log($"[PhysToSpringHarness] done: {message}");
            else Debug.LogError($"[PhysToSpringHarness] failed: {message}");
            var callback = _onFinished;
            _onFinished = null;
            Destroy(gameObject);
            callback?.Invoke(ok, message);
        }

        static bool Guard(Action action, ref string error)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                error = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        }

        internal static void WriteJson(string path, object value)
        {
            var json = JsonConvert.SerializeObject(value, Formatting.Indented);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        /// <summary>
        /// 1 ケース分のリグ・シミュレータ・記録。
        /// PhysBone リグ 1 つに対して、VRM リグを variant 数だけ並べて同時に回す。
        /// </summary>
        sealed class CaseRun : IDisposable
        {
            readonly HarnessCase _case;
            readonly float _dt;
            readonly bool _writeTrace;
            readonly GameObject _root;
            readonly Rig _pbRig;
            readonly List<Variant> _variants = new List<Variant>();
            public readonly int frameCount;
            public readonly bool pbSampleInLateUpdate;

            sealed class Variant
            {
                public Rig rig;
                public VrmSpringSimulator sim;
                public TraceRecorder recorder;
                public List<string> setupWarnings;
            }

            public CaseRun(HarnessCase c, HarnessJob job, Transform parent, Vector3 offset)
            {
                _case = c;
                switch (job.pbSampleTiming)
                {
                    case "lateUpdate": pbSampleInLateUpdate = true; break;
                    case "nextFrame": pbSampleInLateUpdate = false; break;
                    default: throw new ArgumentException($"invalid pbSampleTiming: {job.pbSampleTiming}");
                }
                _dt = 1f / job.fps;
                _writeTrace = job.writeTrace;
                frameCount = Mathf.RoundToInt((c.warmup + c.duration) * job.fps);
                var specs = c.vrmVariants ?? new List<VrmSpec> { c.vrm };
                if (specs.Count == 0) throw new ArgumentException("vrmVariants is empty");

                _root = new GameObject($"Case_{c.name}");
                _root.transform.SetParent(parent, false);
                _root.transform.localPosition = offset;
                // 非アクティブのまま構築し、PhysBone の設定が揃ってから有効化する
                _root.SetActive(false);
                try
                {
                    _pbRig = Rig.Build(c.rig, "pb", _root.transform, Vector3.zero, createEndNodes: false);
                    _pbRig.Base.name = "PhysBoneRig";
                    PhysBoneRigSetup.Setup(_pbRig, c.rig, c.pb, c.colliders);

                    for (var k = 0; k < specs.Count; ++k)
                    {
                        var rig = Rig.Build(c.rig, "vrm", _root.transform, new Vector3(job.rigSpacing * (k + 1), 0f, 0f), createEndNodes: true);
                        rig.Base.name = specs.Count == 1 ? "VrmRig" : $"VrmRig_{k}";
                        var v = new Variant { rig = rig };
                        _variants.Add(v);
                        var spec = specs[k];
                        if (spec.fromConverter)
                        {
                            if (ConverterSpecProvider == null) throw new InvalidOperationException("fromConverter needs the editor converter hook");
                            spec = ConverterSpecProvider(_pbRig.Base, spec);
                        }
                        var springs = VrmRigSetup.Setup(rig, c, spec);
                        v.setupWarnings = VrmRigSetup.NestedSpringWarnings(springs);
                        v.sim = new VrmSpringSimulator(rig.Base, springs);
                        v.recorder = new TraceRecorder(_pbRig, rig, job.fps, c.warmup, _writeTrace);
                    }
                }
                catch
                {
                    Dispose();
                    throw;
                }
                _root.SetActive(true);
            }

            public void BeforeSimulation(int frame)
            {
                MotionDriver.Evaluate(_case.motion, frame * _dt - _case.warmup, out var pos, out var rot);
                _pbRig.Driver.localPosition = pos;
                _pbRig.Driver.localRotation = rot;
                foreach (var v in _variants)
                {
                    v.rig.Driver.localPosition = pos;
                    v.rig.Driver.localRotation = rot;
                }
            }

            /// <summary>呼び出し側で PhysBoneManager.Inst.CompleteJob() を済ませておくこと。</summary>
            public void AfterSimulation(int frame, float deltaTime)
            {
                foreach (var v in _variants)
                {
                    v.sim.Step(_dt);
                    v.recorder.RecordDriverAndVrm(frame, frame * _dt, deltaTime);
                    if (pbSampleInLateUpdate) v.recorder.RecordPhysBone(frame, frame * _dt);
                }
            }

            public void RecordPhysBone(int frame)
            {
                PhysBoneManager.Inst.CompleteJob();
                foreach (var v in _variants) v.recorder.RecordPhysBone(frame, frame * _dt);
            }

            /// <summary>variant 0 を summary.json / trace.csv に、variant が複数なら全件を variants.json に書く。</summary>
            public JobCaseResult Save(string dir)
            {
                Directory.CreateDirectory(dir);
                var hasMotion = _case.motion != null && _case.motion.Count > 0;
                var summaries = new List<CaseSummary>();
                foreach (var v in _variants)
                {
                    var vs = v.recorder.Summarize(_case.name, hasMotion);
                    vs.warnings.InsertRange(0, v.setupWarnings);
                    summaries.Add(vs);
                }
                if (_writeTrace) _variants[0].recorder.WriteTrace(Path.Combine(dir, "trace.csv"));
                var s = summaries[0];
                WriteJson(Path.Combine(dir, "summary.json"), s);
                if (_case.vrmVariants != null) WriteJson(Path.Combine(dir, "variants.json"), summaries);
                foreach (var w in summaries.SelectMany(x => x.warnings).Distinct())
                    Debug.LogWarning($"[PhysToSpringHarness] {_case.name}: {w}");
                return new JobCaseResult
                {
                    name = s.name,
                    meanErrRmsDeg = s.meanErrRmsDeg,
                    maxErrDeg = s.maxErrDeg,
                    meanErrRmsWarmupDeg = s.meanErrRmsWarmupDeg,
                    meanErrRmsMotionDeg = s.meanErrRmsMotionDeg,
                    meanErrFinalDeg = s.meanErrFinalDeg,
                    tipErrRms = s.tipErrRms,
                    warnings = s.warnings,
                };
            }

            public void Dispose()
            {
                foreach (var v in _variants) v.sim?.Dispose();
                if (_root != null) Destroy(_root);
            }
        }
    }
}
