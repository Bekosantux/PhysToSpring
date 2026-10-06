using System.Collections.Generic;
using UnityEngine;

namespace Bekosan.PhysToSpring.Harness
{
    /// <summary>
    /// 1 本のセグメント (head → tail)。tail は実ノードか、head ローカルの仮想点 (PhysBone の endpoint)。
    /// </summary>
    public sealed class RigSegment
    {
        public string name;
        public int chain;
        public Transform head;
        /// <summary>null なら head.TransformPoint(virtualTailLocal) を tail とする。</summary>
        public Transform tail;
        public Vector3 virtualTailLocal;
        /// <summary>構築時の Driver ローカルでの方向 (正規化済み)。</summary>
        public Vector3 restDirection;

        public Vector3 TailWorld => tail != null ? tail.position : head.TransformPoint(virtualTailLocal);
    }

    /// <summary>
    /// Base(静止) → Driver(motion) → Root → チェーン。名前は Root, C{c}_{i}, 末端の仮想/実 tail は {leaf}_end。
    /// </summary>
    public sealed class Rig
    {
        public string label;
        public Transform Base;
        public Transform Driver;
        public Transform Root;
        /// <summary>chains[c][i] = C{c}_{i}。</summary>
        public readonly List<List<Transform>> chains = new List<List<Transform>>();
        /// <summary>chains[c] の末端の tail ノード。endpoint 指定かつ実ノードを作ったときのみ非 null。</summary>
        public readonly List<Transform> endNodes = new List<Transform>();
        /// <summary>記録対象の実ノード (Root と全チェーンノード、実 end ノード)。</summary>
        public readonly List<Transform> nodes = new List<Transform>();
        public readonly List<RigSegment> segments = new List<RigSegment>();
        readonly Dictionary<string, Transform> _byName = new Dictionary<string, Transform>();

        public Transform Find(string name)
        {
            switch (name)
            {
                case null:
                case "":
                    return null;
                case "World":
                case "Base":
                    return Base;
                case "Driver":
                    return Driver;
            }
            if (_byName.TryGetValue(name, out var t)) return t;
            throw new System.ArgumentException($"[{label}] node not found: {name}");
        }

        /// <param name="createEndNodes">true なら endpoint を実ノード {leaf}_end として作る (VRM 用)。</param>
        public static Rig Build(RigSpec spec, string label, Transform parent, Vector3 basePosition, bool createEndNodes)
        {
            var rig = new Rig { label = label };
            rig.Base = NewNode("Base", parent);
            rig.Base.localPosition = basePosition;
            rig.Driver = NewNode("Driver", rig.Base);
            rig.Root = NewNode("Root", rig.Driver);
            rig.Root.localPosition = HarnessMath.ToVector3(spec.rootPosition, Vector3.zero);
            rig.Root.localRotation = Quaternion.Euler(HarnessMath.ToVector3(spec.rootEuler, Vector3.zero));
            rig.Register(rig.Root);

            var endpoint = spec.endpoint != null ? HarnessMath.ToVector3(spec.endpoint, Vector3.zero) : (Vector3?)null;
            var rootPosInDriver = rig.Root.localPosition;

            for (var c = 0; c < spec.chains.Count; ++c)
            {
                var chainSpec = spec.chains[c];
                if (chainSpec.segments < 1) throw new System.ArgumentException($"chain {c}: segments must be >= 1");
                var dir = HarnessMath.ToVector3(chainSpec.direction, Vector3.down);
                if (dir.sqrMagnitude < 1e-12f) throw new System.ArgumentException($"chain {c}: direction is zero");
                dir.Normalize();
                var boneRot = Quaternion.Euler(HarnessMath.ToVector3(chainSpec.boneEuler, Vector3.zero));

                var chain = new List<Transform>();
                var prev = rig.Root;
                for (var i = 0; i < chainSpec.segments; ++i)
                {
                    var node = NewNode($"C{c}_{i}", prev);
                    node.localRotation = boneRot;
                    // 回転を決めてから Driver 空間の直線上に置く
                    node.position = rig.Driver.TransformPoint(rootPosInDriver + dir * (chainSpec.length * (i + 1)));
                    rig.Register(node);
                    chain.Add(node);
                    rig.segments.Add(new RigSegment { name = $"{prev.name}>{node.name}", chain = c, head = prev, tail = node, restDirection = dir });
                    prev = node;
                }
                rig.chains.Add(chain);

                Transform endNode = null;
                if (endpoint.HasValue)
                {
                    var leaf = chain[chain.Count - 1];
                    var restDir = rig.Driver.InverseTransformDirection(leaf.TransformVector(endpoint.Value)).normalized;
                    if (createEndNodes)
                    {
                        endNode = NewNode($"{leaf.name}_end", leaf);
                        endNode.localPosition = endpoint.Value;
                        rig.Register(endNode);
                    }
                    rig.segments.Add(new RigSegment
                    {
                        name = $"{leaf.name}>{leaf.name}_end",
                        chain = c,
                        head = leaf,
                        tail = endNode,
                        virtualTailLocal = endpoint.Value,
                        restDirection = restDir,
                    });
                }
                rig.endNodes.Add(endNode);
            }
            return rig;
        }

        void Register(Transform t)
        {
            if (_byName.ContainsKey(t.name)) throw new System.ArgumentException($"duplicated node name: {t.name}");
            _byName.Add(t.name, t);
            nodes.Add(t);
        }

        static Transform NewNode(string name, Transform parent)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }
    }
}
