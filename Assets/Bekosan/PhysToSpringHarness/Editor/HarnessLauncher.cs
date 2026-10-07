using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Bekosan.PhysToSpring.Harness.Editor
{
    /// <summary>
    /// HarnessJobs/requests/*.json を監視し、Play Mode に入ってジョブを実行する。
    ///
    ///   HarnessJobs/requests/{id}.json  投入 (書き込み途中は .tmp にしておき rename する)
    ///   HarnessJobs/running/{id}.json   実行中
    ///   HarnessJobs/done/{id}.json      成功
    ///   HarnessJobs/failed/{id}.json    失敗
    ///   HarnessJobs/results/{id}/       status.json, summary.json, job.json, {case}/trace.csv, {case}/summary.json
    ///
    /// エディタを開いたまま外部から投入できる。投入時に AssetDatabase.Refresh するので、
    /// スクリプトを書き換えた直後でもコンパイル後の状態で実行される。
    /// ジョブのために入った Play Mode は、次の request が KeepPlayingIdle 秒以内に来れば抜けずにそのまま実行する
    /// (ジョブごとの Play Mode 出入りのドメインリロードを避ける)。Assets/Bekosan 以下の .cs が更新されたら抜けて再コンパイルする。
    /// バッチモード: -executeMethod Bekosan.PhysToSpring.Harness.Editor.HarnessLauncher.RunBatch -harnessJob path/to/job.json
    /// </summary>
    [InitializeOnLoad]
    public static class HarnessLauncher
    {
        const string MenuRoot = "Tools/PhysToSpring Harness/";
        const string PrefEnabled = "Bekosan.PhysToSpring.Harness.WatcherEnabled";
        const string SessionRunning = "Bekosan.PhysToSpring.Harness.RunningId";
        const string SessionBatch = "Bekosan.PhysToSpring.Harness.Batch";
        const string SessionOwnedPlay = "Bekosan.PhysToSpring.Harness.OwnedPlay";
        const string SessionPlayStartTicks = "Bekosan.PhysToSpring.Harness.PlayStartTicks";
        const double PollInterval = 1.0;
        const double KeepPlayingIdle = 5.0;

        static double _nextPoll;
        static double _idleSince;
        static string _waitingReason;

        public static string JobsRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "HarnessJobs"));
        static string Dir(string name) => Path.Combine(JobsRoot, name);

        static bool WatcherEnabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, true);
            set => EditorPrefs.SetBool(PrefEnabled, value);
        }

        static HarnessLauncher()
        {
            EditorApplication.update += Poll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _nextPoll) return;
            _nextPoll = EditorApplication.timeSinceStartup + PollInterval;

            // 変換器のセルフテスト (Edit Mode で即実行)
            var selfTest = Path.Combine(JobsRoot, "selftest.request");
            if (File.Exists(selfTest) && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
            {
                AssetDatabase.Refresh();
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                File.Delete(selfTest);
                ConverterSelfTest.Run(Path.Combine(Dir("results"), "selftest.json"));
                Debug.Log("[PhysToSpringHarness] selftest done");
            }

            // 開いているシーンの PhysBone / VRM 設定のダンプ
            var diag = Path.Combine(JobsRoot, "diag.request");
            if (File.Exists(diag) && !EditorApplication.isCompiling)
            {
                AssetDatabase.Refresh();
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                File.Delete(diag);
                SceneDiag.Run(Path.Combine(Dir("results"), "diag.json"));
                Debug.Log("[PhysToSpringHarness] diag done");
            }

            // 実アバターの比較 (Play Mode に入る)
            var probe = Path.Combine(JobsRoot, "avatarprobe.request");
            if (File.Exists(probe) && !EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isCompiling)
            {
                AssetDatabase.Refresh();
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                File.Delete(probe);
                AvatarProbeLauncher.Start();
                return;
            }

            var requests = Dir("requests");
            var next = Directory.Exists(requests)
                ? Directory.GetFiles(requests, "*.json").OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault()
                : null;
            if (EditorApplication.isPlaying && SessionState.GetBool(SessionOwnedPlay, false))
            {
                PollInOwnedPlay(next);
                return;
            }
            if (next == null)
            {
                Waiting(null);
                return;
            }

            // 待機理由は request があるときだけ、変化したときに 1 回ログに出す (Editor.log から状態を追えるように)
            if (!WatcherEnabled && !SessionState.GetBool(SessionBatch, false)) { Waiting("watcher is disabled (Tools/PhysToSpring Harness/Watch Requests)"); return; }
            if (!string.IsNullOrEmpty(SessionState.GetString(SessionRunning, ""))) { Waiting("another job is running"); return; }
            if (EditorApplication.isPlayingOrWillChangePlaymode) { Waiting("editor is in Play Mode; stop it to run queued jobs"); return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { Waiting("compiling / importing"); return; }

            // 外部で書き換えたスクリプトを反映する。コンパイルが走ったら次回のポーリング (リロード後) で続行。
            AssetDatabase.Refresh();
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) { Waiting("compiling / importing"); return; }
            Waiting(null);

            if (EditorUtility.scriptCompilationFailed)
            {
                Fail(next, Path.GetFileNameWithoutExtension(next), "script compilation failed");
                return;
            }
            var id = Accept(next);
            if (id == null) return;
            SessionState.SetBool(SessionOwnedPlay, true);
            SessionState.SetString(SessionPlayStartTicks, DateTime.UtcNow.Ticks.ToString());
            EditorApplication.isPlaying = true;
        }

        /// <summary>自分で入った Play Mode 中: 次の request があればそのまま実行し、無ければしばらく待って抜ける。</summary>
        static void PollInOwnedPlay(string next)
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(SessionRunning, ""))) return;
            if (ScriptsChangedSincePlay())
            {
                Debug.Log("[PhysToSpringHarness] scripts changed; exiting Play Mode to recompile");
                EditorApplication.isPlaying = false;
                return;
            }
            if (next == null)
            {
                if (EditorApplication.timeSinceStartup - _idleSince > KeepPlayingIdle) EditorApplication.isPlaying = false;
                return;
            }
            var id = Accept(next);
            if (id != null) StartRunner(id);
        }

        static bool ScriptsChangedSincePlay()
        {
            if (!long.TryParse(SessionState.GetString(SessionPlayStartTicks, ""), out var ticks)) return false;
            var root = Path.Combine(Application.dataPath, "Bekosan");
            return Directory.Exists(root) && Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Any(p => File.GetLastWriteTimeUtc(p).Ticks > ticks);
        }

        /// <summary>request を検証して running に移す。失敗なら null。</summary>
        static string Accept(string next)
        {
            var id = Path.GetFileNameWithoutExtension(next);
            try
            {
                LoadJob(next);
            }
            catch (Exception e)
            {
                Fail(next, id, $"invalid job json: {e.Message}");
                return null;
            }

            var running = Path.Combine(Dir("running"), id + ".json");
            Directory.CreateDirectory(Dir("running"));
            File.Delete(running);
            File.Move(next, running);

            var resultDir = Path.Combine(Dir("results"), id);
            if (Directory.Exists(resultDir)) Directory.Delete(resultDir, true);
            Directory.CreateDirectory(resultDir);
            WriteStatus(id, "running", "");

            SessionState.SetString(SessionRunning, id);
            Debug.Log($"[PhysToSpringHarness] start {id}");
            return id;
        }

        static void Waiting(string reason)
        {
            if (reason == _waitingReason) return;
            _waitingReason = reason;
            if (reason != null) Debug.Log($"[PhysToSpringHarness] waiting: {reason}");
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode) SessionState.EraseBool(SessionOwnedPlay);
            var id = SessionState.GetString(SessionRunning, "");
            if (string.IsNullOrEmpty(id)) return;

            switch (change)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    StartRunner(id);
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    // 完了前に Play Mode が止められた
                    Complete(id, false, "aborted (play mode exited)");
                    break;
            }
        }

        static void StartRunner(string id)
        {
            HarnessJob job;
            try
            {
                job = LoadJob(Path.Combine(Dir("running"), id + ".json"));
            }
            catch (Exception e)
            {
                Complete(id, false, $"invalid job json: {e.Message}");
                return;
            }
            // 終わっても Play Mode は抜けない (次の request を PollInOwnedPlay で拾う)
            HarnessRunner.Launch(job, Path.Combine(Dir("results"), id), (ok, message) => Complete(id, ok, message));
        }

        static void Complete(string id, bool ok, string message)
        {
            SessionState.EraseString(SessionRunning);
            var running = Path.Combine(Dir("running"), id + ".json");
            var dest = Path.Combine(Dir(ok ? "done" : "failed"), id + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            if (File.Exists(running))
            {
                File.Delete(dest);
                File.Move(running, dest);
            }
            WriteStatus(id, ok ? "done" : "failed", message);
            Debug.Log($"[PhysToSpringHarness] {(ok ? "done" : "failed")} {id}: {message}");
            _idleSince = EditorApplication.timeSinceStartup;

            if (SessionState.GetBool(SessionBatch, false))
            {
                SessionState.EraseBool(SessionBatch);
                EditorApplication.delayCall += () => EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        static void Fail(string requestPath, string id, string message)
        {
            var dest = Path.Combine(Dir("failed"), id + ".json");
            Directory.CreateDirectory(Dir("failed"));
            File.Delete(dest);
            File.Move(requestPath, dest);
            Directory.CreateDirectory(Path.Combine(Dir("results"), id));
            WriteStatus(id, "failed", message);
            Debug.LogError($"[PhysToSpringHarness] failed {id}: {message}");
            if (SessionState.GetBool(SessionBatch, false))
            {
                SessionState.EraseBool(SessionBatch);
                EditorApplication.Exit(1);
            }
        }

        static void WriteStatus(string id, string state, string message)
        {
            var path = Path.Combine(Dir("results"), id, "status.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = JsonConvert.SerializeObject(new
            {
                id,
                state,
                message,
                time = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffK"),
            }, Formatting.Indented);
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        static HarnessJob LoadJob(string path)
        {
            var job = HarnessJob.FromJson(File.ReadAllText(path));
            if (job.fps <= 0f) throw new InvalidDataException("fps must be > 0");
            if (job.cases == null || job.cases.Count == 0) throw new InvalidDataException("no cases");
            return job;
        }

        /// <summary>request として投入する。id は時刻 + 元ファイル名。</summary>
        public static string Enqueue(string jobPath)
        {
            var id = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Path.GetFileNameWithoutExtension(jobPath)}";
            Directory.CreateDirectory(Dir("requests"));
            var tmp = Path.Combine(Dir("requests"), id + ".json.tmp");
            File.Copy(jobPath, tmp, true);
            File.Move(tmp, Path.Combine(Dir("requests"), id + ".json"));
            return id;
        }

        /// <summary>バッチモード用。-harnessJob の job を投入し、完了したら終了する。</summary>
        public static void RunBatch()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-harnessJob");
            if (index < 0 || index + 1 >= args.Length)
            {
                Debug.LogError("[PhysToSpringHarness] -harnessJob <path> is required");
                EditorApplication.Exit(2);
                return;
            }
            var id = Enqueue(Path.GetFullPath(args[index + 1]));
            Debug.Log($"[PhysToSpringHarness] batch enqueue {id}");
            SessionState.SetBool(SessionBatch, true);
        }

        /// <summary>バッチモード用。変換器のセルフテストを実行して results/selftest.json に書き、終了する。</summary>
        public static void RunSelfTestBatch()
        {
            ConverterSelfTest.Run(Path.Combine(Dir("results"), "selftest.json"));
            Debug.Log("[PhysToSpringHarness] selftest done");
            EditorApplication.Exit(0);
        }

        [MenuItem(MenuRoot + "Enqueue Job...")]
        static void EnqueueJobMenu()
        {
            var path = EditorUtility.OpenFilePanel("Harness job", Dir("examples"), "json");
            if (string.IsNullOrEmpty(path)) return;
            Debug.Log($"[PhysToSpringHarness] enqueued {Enqueue(path)}");
        }

        [MenuItem(MenuRoot + "Open Jobs Folder")]
        static void OpenFolder()
        {
            Directory.CreateDirectory(JobsRoot);
            EditorUtility.RevealInFinder(JobsRoot + Path.DirectorySeparatorChar);
        }

        [MenuItem(MenuRoot + "Watch Requests")]
        static void ToggleWatcher() => WatcherEnabled = !WatcherEnabled;

        [MenuItem(MenuRoot + "Watch Requests", true)]
        static bool ToggleWatcherValidate()
        {
            Menu.SetChecked(MenuRoot + "Watch Requests", WatcherEnabled);
            return true;
        }
    }
}
