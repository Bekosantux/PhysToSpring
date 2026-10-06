using System;
using System.Collections.Generic;
using System.Linq;
using Bekosan.PhysToSpring.Editor;
using UnityEditor;
using UnityEngine;

namespace Bekosan.PhysToSpring.Harness.Editor
{
    /// <summary>
    /// VrmSpec.fromConverter: PhysBone リグに変換器の Reader をかけ、その結果を VrmSpec に入れる。
    /// VRM リグは同じ名前のノードを持つので、spring は名前で指定する (endpoint の tail は {leaf}_end)。
    /// </summary>
    [InitializeOnLoad]
    static class ConverterSpecHook
    {
        static ConverterSpecHook()
        {
            HarnessRunner.ConverterSpecProvider = Provide;
        }

        static VrmSpec Provide(Transform pbRigBase, VrmSpec template)
        {
            var report = new ConversionReport();
            var plans = PhysBoneReader.Read(pbRigBase, report);
            if (report.HasError) throw new ArgumentException("converter: " + report.Summary());

            var spec = template.Clone();
            spec.fromConverter = false;
            spec.springs = new List<List<string>>();
            spec.perSpring = new List<Dictionary<string, float[]>>();
            var c = plans.Select(p => p.Center).FirstOrDefault(x => x != null);
            spec.center = c == null ? "" : c == pbRigBase ? "Base" : c.name;
            var first = plans.Count > 0 && plans[0].Joints.Count > 0 ? plans[0].Joints[0] : default;
            spec.limitType = first.LimitType.ToString();
            spec.limitOffsetEuler = ToArray(first.LimitOffset.eulerAngles);
            foreach (var plan in plans)
            {
                var names = plan.Bones.Select(b => b.name).ToList();
                if (plan.EndpointLocal.HasValue) names.Add(plan.Bones[plan.Bones.Count - 1].name + "_end");
                spec.springs.Add(names);
                var j = plan.Joints;
                spec.perSpring.Add(new Dictionary<string, float[]>
                {
                    ["stiffness"] = j.Select(x => x.Params.Stiffness).ToArray(),
                    ["dragForce"] = j.Select(x => x.Params.DragForce).ToArray(),
                    ["gravityPower"] = j.Select(x => x.Params.GravityPower).ToArray(),
                    ["hitRadius"] = j.Select(x => x.HitRadius).ToArray(),
                    ["limitAngle"] = j.Select(x => x.Pitch * Mathf.Rad2Deg).ToArray(),
                    ["pitch"] = j.Select(x => x.Pitch * Mathf.Rad2Deg).ToArray(),
                    ["yaw"] = j.Select(x => x.Yaw * Mathf.Rad2Deg).ToArray(),
                });
            }
            foreach (var e in report.Entries.Where(e => e.Level == ReportLevel.Warning))
            {
                Debug.Log("[PhysToSpringHarness] converter: " + e.Message);
            }
            return spec;
        }

        static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };
    }
}
