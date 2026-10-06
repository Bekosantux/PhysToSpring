using UnityEngine;

namespace Bekosan.PhysToSpring.Harness
{
    static class HarnessMath
    {
        public static Vector3 ToVector3(float[] v, Vector3 fallback)
        {
            if (v == null || v.Length < 3) return fallback;
            return new Vector3(v[0], v[1], v[2]);
        }

        /// <summary>
        /// 2 方向のなす角 [deg]。どちらかがほぼ 0 なら 0。
        /// Vector3.Angle (acos) は小角度で精度が落ちるので atan2 で求める。
        /// </summary>
        public static float AngleDeg(Vector3 a, Vector3 b)
        {
            if (a.sqrMagnitude < 1e-20f || b.sqrMagnitude < 1e-20f) return 0f;
            double ax = a.x, ay = a.y, az = a.z, bx = b.x, by = b.y, bz = b.z;
            double cx = ay * bz - az * by, cy = az * bx - ax * bz, cz = ax * by - ay * bx;
            double cross = System.Math.Sqrt(cx * cx + cy * cy + cz * cz);
            double dot = ax * bx + ay * by + az * bz;
            return (float)(System.Math.Atan2(cross, dot) * (180.0 / System.Math.PI));
        }
    }
}
