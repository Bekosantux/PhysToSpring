using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Bekosan.PhysToSpring.Harness
{
    public sealed class SegmentSummary
    {
        public string name;
        /// <summary>PhysBone と VRM のセグメント方向の差 [deg]。</summary>
        public float errRmsDeg;
        public float errMaxDeg;
        /// <summary>warmup 区間 (静置) の RMS。重力たわみの一致を見る。</summary>
        public float errRmsWarmupDeg;
        /// <summary>motion 区間の RMS。</summary>
        public float errRmsMotionDeg;
        /// <summary>最後の finalWindow 秒の平均。</summary>
        public float errFinalDeg;
        /// <summary>初期配置方向からのずれ [deg]。</summary>
        public float pbDevMaxDeg;
        public float vrmDevMaxDeg;
        public float pbDevFinalDeg;
        public float vrmDevFinalDeg;
    }

    public sealed class CaseSummary
    {
        public string name;
        public int frames;
        public float fps;
        public float warmup;
        public float finalWindow;
        public float meanErrRmsDeg;
        public float maxErrDeg;
        public float meanErrRmsWarmupDeg;
        public float meanErrRmsMotionDeg;
        public float meanErrFinalDeg;
        /// <summary>各チェーン末端 tail 位置の差の RMS [m] (Driver ローカル)。</summary>
        public float tipErrRms;
        public float pbDevMaxDeg;
        public float vrmDevMaxDeg;
        public List<SegmentSummary> segments = new List<SegmentSummary>();
        public List<string> warnings = new List<string>();
    }

    /// <summary>
    /// 毎フレームの姿勢を trace.csv に書き、PhysBone / VRM のセグメント方向の差を集計する。
    /// 位置・回転は Driver ローカル。driver 行のみ Base ローカル。
    /// </summary>
    public sealed class TraceRecorder
    {
        readonly Rig _pb;
        readonly Rig _vrm;
        readonly float _fps;
        readonly float _warmup;
        /// <summary>null なら CSV を作らない。</summary>
        readonly StringBuilder _csv;
        readonly List<float> _times = new List<float>();
        readonly List<Vector3[]> _pbDirs = new List<Vector3[]>();
        readonly List<Vector3[]> _vrmDirs = new List<Vector3[]>();
        readonly List<Vector3[]> _pbTips = new List<Vector3[]>();
        readonly List<Vector3[]> _vrmTips = new List<Vector3[]>();
        readonly int[] _tipSegments;
        int _badDeltaFrames;

        public const float FinalWindow = 0.5f;

        public TraceRecorder(Rig pb, Rig vrm, float fps, float warmup, bool writeTrace = true)
        {
            if (pb.segments.Count != vrm.segments.Count)
                throw new System.InvalidOperationException("PhysBone / VRM rigs have different segment layouts");
            _pb = pb;
            _vrm = vrm;
            _fps = fps;
            _warmup = warmup;
            // 各チェーンの最後のセグメント
            _tipSegments = Enumerable.Range(0, pb.segments.Count)
                .Where(i => i == pb.segments.Count - 1 || pb.segments[i].chain != pb.segments[i + 1].chain)
                .ToArray();
            if (writeTrace)
            {
                _csv = new StringBuilder(1 << 20);
                _csv.Append("frame,t,rig,bone,px,py,pz,qx,qy,qz,qw\n");
            }
        }

        /// <summary>Driver と VRM リグを記録する。PhysBone は記録タイミングが異なり得るので RecordPhysBone で別に記録する。</summary>
        public void RecordDriverAndVrm(int frame, float t, float deltaTime)
        {
            if (Mathf.Abs(deltaTime - 1f / _fps) > 1e-5f) ++_badDeltaFrames;
            _times.Add(t);

            var driver = _vrm.Driver;
            if (_csv != null) AppendRow(frame, t, "driver", "Driver", driver.localPosition, driver.localRotation);
            RecordRig(frame, t, "vrm", _vrm, _vrmDirs, _vrmTips);
        }

        public void RecordPhysBone(int frame, float t)
        {
            RecordRig(frame, t, "pb", _pb, _pbDirs, _pbTips);
        }

        void RecordRig(int frame, float t, string label, Rig rig, List<Vector3[]> dirs, List<Vector3[]> tips)
        {
            var driver = rig.Driver;
            if (_csv != null)
            {
                var invRot = Quaternion.Inverse(driver.rotation);
                foreach (var node in rig.nodes)
                {
                    AppendRow(frame, t, label, node.name, driver.InverseTransformPoint(node.position), invRot * node.rotation);
                }
                // PhysBone の endpoint は Transform が無いので仮想点として出力する
                foreach (var seg in rig.segments)
                {
                    if (seg.tail == null && seg.virtualTailLocal != Vector3.zero)
                    {
                        AppendRow(frame, t, label, $"{seg.head.name}_end", driver.InverseTransformPoint(seg.TailWorld), invRot * seg.head.rotation);
                    }
                }
            }

            var d = new Vector3[rig.segments.Count];
            for (var i = 0; i < d.Length; ++i)
            {
                var seg = rig.segments[i];
                d[i] = driver.InverseTransformDirection(seg.TailWorld - seg.head.position);
            }
            dirs.Add(d);
            tips.Add(_tipSegments.Select(i => driver.InverseTransformPoint(rig.segments[i].TailWorld)).ToArray());
        }

        void AppendRow(int frame, float t, string rig, string bone, Vector3 p, Quaternion q)
        {
            var ci = CultureInfo.InvariantCulture;
            _csv.Append(frame).Append(',')
                .Append(t.ToString("G9", ci)).Append(',')
                .Append(rig).Append(',')
                .Append(bone).Append(',')
                .Append(p.x.ToString("G9", ci)).Append(',')
                .Append(p.y.ToString("G9", ci)).Append(',')
                .Append(p.z.ToString("G9", ci)).Append(',')
                .Append(q.x.ToString("G9", ci)).Append(',')
                .Append(q.y.ToString("G9", ci)).Append(',')
                .Append(q.z.ToString("G9", ci)).Append(',')
                .Append(q.w.ToString("G9", ci)).Append('\n');
        }

        public void WriteTrace(string path)
        {
            if (_csv == null) throw new System.InvalidOperationException("trace was not recorded (writeTrace = false)");
            File.WriteAllText(path, _csv.ToString(), new UTF8Encoding(false));
        }

        public CaseSummary Summarize(string caseName, bool hasMotion)
        {
            var n = Mathf.Min(_times.Count, _pbDirs.Count);
            var s = new CaseSummary
            {
                name = caseName,
                frames = n,
                fps = _fps,
                warmup = _warmup,
                finalWindow = FinalWindow,
            };
            if (n == 0)
            {
                s.warnings.Add("no frames recorded");
                return s;
            }
            var finalStart = Mathf.Max(0, n - Mathf.Max(1, Mathf.RoundToInt(FinalWindow * _fps)));

            for (var k = 0; k < _pb.segments.Count; ++k)
            {
                var rest = _pb.segments[k].restDirection;
                double sq = 0, sqW = 0, sqM = 0, fin = 0, pbFin = 0, vrmFin = 0;
                int nW = 0, nM = 0;
                float max = 0, pbDevMax = 0, vrmDevMax = 0;
                for (var f = 0; f < n; ++f)
                {
                    var e = HarnessMath.AngleDeg(_pbDirs[f][k], _vrmDirs[f][k]);
                    var pbDev = HarnessMath.AngleDeg(_pbDirs[f][k], rest);
                    var vrmDev = HarnessMath.AngleDeg(_vrmDirs[f][k], rest);
                    sq += e * e;
                    if (_times[f] < _warmup) { sqW += e * e; ++nW; }
                    else { sqM += e * e; ++nM; }
                    max = Mathf.Max(max, e);
                    pbDevMax = Mathf.Max(pbDevMax, pbDev);
                    vrmDevMax = Mathf.Max(vrmDevMax, vrmDev);
                    if (f >= finalStart) { fin += e; pbFin += pbDev; vrmFin += vrmDev; }
                }
                var nf = n - finalStart;
                s.segments.Add(new SegmentSummary
                {
                    name = _pb.segments[k].name,
                    errRmsDeg = (float)System.Math.Sqrt(sq / n),
                    errMaxDeg = max,
                    errRmsWarmupDeg = nW > 0 ? (float)System.Math.Sqrt(sqW / nW) : 0f,
                    errRmsMotionDeg = nM > 0 ? (float)System.Math.Sqrt(sqM / nM) : 0f,
                    errFinalDeg = (float)(fin / nf),
                    pbDevMaxDeg = pbDevMax,
                    vrmDevMaxDeg = vrmDevMax,
                    pbDevFinalDeg = (float)(pbFin / nf),
                    vrmDevFinalDeg = (float)(vrmFin / nf),
                });
            }

            if (s.segments.Count > 0)
            {
                s.meanErrRmsDeg = s.segments.Average(x => x.errRmsDeg);
                s.maxErrDeg = s.segments.Max(x => x.errMaxDeg);
                s.meanErrRmsWarmupDeg = s.segments.Average(x => x.errRmsWarmupDeg);
                s.meanErrRmsMotionDeg = s.segments.Average(x => x.errRmsMotionDeg);
                s.meanErrFinalDeg = s.segments.Average(x => x.errFinalDeg);
                s.pbDevMaxDeg = s.segments.Max(x => x.pbDevMaxDeg);
                s.vrmDevMaxDeg = s.segments.Max(x => x.vrmDevMaxDeg);
            }

            double tipSq = 0;
            var tipCount = 0;
            for (var f = 0; f < n; ++f)
            {
                for (var i = 0; i < _pbTips[f].Length; ++i)
                {
                    tipSq += (_pbTips[f][i] - _vrmTips[f][i]).sqrMagnitude;
                    ++tipCount;
                }
            }
            s.tipErrRms = tipCount > 0 ? (float)System.Math.Sqrt(tipSq / tipCount) : 0f;

            if (_badDeltaFrames > 0)
                s.warnings.Add($"{_badDeltaFrames} frames had deltaTime != 1/fps");
            if (hasMotion && s.pbDevMaxDeg < 1e-3f)
                s.warnings.Add("PhysBone rig did not move (not simulated?)");
            if (hasMotion && s.vrmDevMaxDeg < 1e-3f)
                s.warnings.Add("VRM rig did not move (not simulated?)");
            return s;
        }
    }
}
