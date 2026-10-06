using System.Collections.Generic;
using UnityEngine;

namespace Bekosan.PhysToSpring.Harness
{
    /// <summary>
    /// MotionSpec から Driver の Base ローカル姿勢を求める。
    /// </summary>
    public static class MotionDriver
    {
        /// <param name="tm">motion 開始 (warmup 終了) からの経過時間 [s]。負なら静止。</param>
        public static void Evaluate(List<MotionSpec> motion, float tm, out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (motion == null || tm < 0f) return;

            foreach (var m in motion)
            {
                var u = tm - m.start;
                if (u < 0f) continue;
                var axis = HarnessMath.ToVector3(m.axis, Vector3.right);
                var axisN = axis.sqrMagnitude > 0f ? axis.normalized : Vector3.right;

                switch (m.type)
                {
                    case "step":
                        position += HarnessMath.ToVector3(m.position, Vector3.zero);
                        rotation *= Quaternion.Euler(HarnessMath.ToVector3(m.euler, Vector3.zero));
                        break;
                    case "sinePos":
                        position += axisN * (m.amplitude * Mathf.Sin(2f * Mathf.PI * m.frequency * u));
                        break;
                    case "sineRot":
                        rotation *= Quaternion.AngleAxis(m.amplitude * Mathf.Sin(2f * Mathf.PI * m.frequency * u), axisN);
                        break;
                    case "chirpPos":
                        position += axisN * (m.amplitude * Mathf.Sin(ChirpPhase(m, u)));
                        break;
                    case "chirpRot":
                        rotation *= Quaternion.AngleAxis(m.amplitude * Mathf.Sin(ChirpPhase(m, u)), axisN);
                        break;
                    case "rampPos":
                        position += axisN * (m.amplitude * Ramp(m, u));
                        break;
                    case "rampRot":
                        rotation *= Quaternion.AngleAxis(m.amplitude * Ramp(m, u), axisN);
                        break;
                    default:
                        throw new System.ArgumentException($"unknown motion type: {m.type}");
                }
            }
        }

        /// <summary>length 秒で frequency → frequencyEnd に線形掃引し、以降は frequencyEnd で位相連続に続ける。</summary>
        static float ChirpPhase(MotionSpec m, float u)
        {
            var len = Mathf.Max(m.length, 1e-6f);
            var f0 = m.frequency;
            var f1 = m.frequencyEnd;
            if (u <= len) return 2f * Mathf.PI * (f0 * u + (f1 - f0) * u * u / (2f * len));
            var phaseAtEnd = f0 * len + (f1 - f0) * len / 2f;
            return 2f * Mathf.PI * (phaseAtEnd + f1 * (u - len));
        }

        static float Ramp(MotionSpec m, float u)
        {
            var s = m.length > 0f ? Mathf.Clamp01(u / m.length) : 1f;
            return s * s * (3f - 2f * s);
        }
    }
}
