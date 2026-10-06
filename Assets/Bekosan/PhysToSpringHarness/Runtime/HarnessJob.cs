using System.Collections.Generic;
using Newtonsoft.Json;

namespace Bekosan.PhysToSpring.Harness
{
    /// <summary>
    /// 計測ジョブ。JSON (Newtonsoft) で読み書きする。
    /// 角度はすべて degree、長さは meter。
    /// </summary>
    public sealed class HarnessJob
    {
        public string name = "job";
        public float fps = 60f;
        /// <summary>PhysBone リグと VRM リグを X 方向にずらす距離。記録は各リグの Base ローカルなので結果には影響しない。</summary>
        public float rigSpacing = 0.5f;
        /// <summary>
        /// PhysBone リグを記録するタイミング。
        /// lateUpdate: 同じフレームの LateUpdate 末尾 / nextFrame: 次フレームの Update 後 (Driver 更新前)。
        /// PhysBone の結果は LateUpdate (order 32000) の後で Transform に書き戻されるため、既定は nextFrame。
        /// lateUpdate では PhysBone が 1 フレーム遅れて見える (計測上の見かけの遅れ)。
        /// </summary>
        public string pbSampleTiming = "nextFrame";
        /// <summary>false なら trace.csv を書かない (最適化ループで variant が多いとき用)。</summary>
        public bool writeTrace = true;
        /// <summary>true なら全ケースのリグを同時に並べて回す (ケースごとに Z 方向へ rigSpacing ずつずらす)。</summary>
        public bool parallelCases;
        public List<HarnessCase> cases = new List<HarnessCase>();

        /// <summary>
        /// 既定値の List を JSON の内容で置き換え (追記しない)、未知のキーはエラーにする。
        /// </summary>
        public static HarnessJob FromJson(string json)
        {
            var settings = new JsonSerializerSettings
            {
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                MissingMemberHandling = MissingMemberHandling.Error,
            };
            return JsonConvert.DeserializeObject<HarnessJob>(json, settings)
                   ?? throw new System.IO.InvalidDataException("empty job");
        }
    }

    public sealed class HarnessCase
    {
        public string name = "case";
        public RigSpec rig = new RigSpec();
        public PhysBoneSpec pb = new PhysBoneSpec();
        public VrmSpec vrm = new VrmSpec();
        /// <summary>
        /// null 以外なら vrm の代わりにこれらを同時に評価する (1 つの PhysBone リグに対して VRM リグを variant 数だけ並べる)。
        /// 結果は {case}/variants.json に variant 順で書く。
        /// </summary>
        public List<VrmSpec> vrmVariants;
        /// <summary>PhysBone 側のコライダー。vrm.colliders が null ならここから VRM 側を暫定変換する。</summary>
        public List<ColliderSpec> colliders = new List<ColliderSpec>();
        /// <summary>Driver に与える動き。各要素の位置は加算、回転は順に乗算する。</summary>
        public List<MotionSpec> motion = new List<MotionSpec>();
        /// <summary>動きを始める前の静置時間 [s]。この間も記録する。</summary>
        public float warmup = 1f;
        /// <summary>動き開始後の記録時間 [s]。</summary>
        public float duration = 5f;
    }

    /// <summary>
    /// Base(静止) → Driver(motion) → Root → チェーン。
    /// 各チェーンは Root の子として始まり、segments 個のノードを持つ。
    /// </summary>
    public sealed class RigSpec
    {
        public List<ChainSpec> chains = new List<ChainSpec> { new ChainSpec() };
        /// <summary>Driver に対する Root の位置。</summary>
        public float[] rootPosition = { 0f, 0f, 0f };
        /// <summary>Driver に対する Root の回転。</summary>
        public float[] rootEuler = { 0f, 0f, 0f };
        /// <summary>
        /// null 以外なら PhysBone の endpointPosition に設定し、
        /// VRM リグでは各末端ノードの子としてこのローカル位置に tail ノードを追加する。
        /// </summary>
        public float[] endpoint;
        /// <summary>true なら Driver に VRCAvatarDescriptor を付ける (Immobile World の基準になるアバタールートの代わり)。</summary>
        public bool avatarDescriptorOnDriver;
    }

    public sealed class ChainSpec
    {
        /// <summary>Root の下に並ぶノード数 (= Root からのセグメント数)。</summary>
        public int segments = 4;
        public float length = 0.05f;
        /// <summary>Driver 空間でのチェーンの伸びる方向。チェーンは常に直線に配置される。</summary>
        public float[] direction = { 0f, -1f, 0f };
        /// <summary>各ノードの親に対するローカル回転。位置は direction 上に保たれる。</summary>
        public float[] boneEuler = { 0f, 0f, 0f };
    }

    public sealed class PhysBoneSpec
    {
        /// <summary>Simplified | Advanced</summary>
        public string integrationType = "Simplified";
        public float pull = 0.2f;
        /// <summary>Simplified では Spring、Advanced では Momentum として扱われる。</summary>
        public float spring = 0.2f;
        public float stiffness = 0.2f;
        public float gravity = 0f;
        public float gravityFalloff = 0f;
        /// <summary>AllMotion | World</summary>
        public string immobileType = "AllMotion";
        public float immobile = 0f;
        public float radius = 0f;
        /// <summary>Ignore | First | Average</summary>
        public string multiChildType = "Ignore";
        /// <summary>None | Angle | Hinge | Polar</summary>
        public string limitType = "None";
        public float maxAngleX = 45f;
        public float maxAngleZ = 45f;
        public float[] limitRotation = { 0f, 0f, 0f };
        public List<CurveSpec> curves = new List<CurveSpec>();
    }

    public sealed class CurveSpec
    {
        /// <summary>pull | spring | stiffness | gravity | gravityFalloff | immobile | radius | maxAngleX | maxAngleZ</summary>
        public string param;
        /// <summary>[time0, value0, time1, value1, ...]</summary>
        public float[] keys;
    }

    public sealed class VrmSpec
    {
        public float stiffness = 1f;
        public float gravityPower = 0f;
        public float[] gravityDir = { 0f, -1f, 0f };
        public float dragForce = 0.4f;
        public float hitRadius = 0f;
        /// <summary>
        /// joint 単位の上書き。key は stiffness | gravityPower | dragForce | hitRadius | limitAngle | pitch | yaw。
        /// 配列の添字は spring 内の joint の順番。足りない分は最後の値を使う。
        /// </summary>
        public Dictionary<string, float[]> perJoint = new Dictionary<string, float[]>();
        /// <summary>null 以外なら spring ごとの perJoint (springs と同じ順)。該当 spring のキーは perJoint より優先する。</summary>
        public List<Dictionary<string, float[]>> perSpring;
        /// <summary>
        /// true なら PhysBone リグに変換器 (Bekosan.PhysToSpring.Editor の PhysBoneReader) をかけた結果で
        /// springs / perSpring / limit / center を置き換える (他の値はこの spec のまま)。エディタでのみ有効。
        /// </summary>
        public bool fromConverter;
        /// <summary>None | Cone | Hinge | Spherical</summary>
        public string limitType = "None";
        /// <summary>Cone / Hinge の角度 [deg]。</summary>
        public float limitAngle = 180f;
        /// <summary>Spherical の pitch [deg]。</summary>
        public float pitch = 180f;
        /// <summary>Spherical の yaw [deg]。</summary>
        public float yaw = 90f;
        public float[] limitOffsetEuler = { 0f, 0f, 0f };
        /// <summary>空なら center なし。Base | Driver | ノード名。</summary>
        public string center = "";
        /// <summary>joint 名の並び。null なら RigSpec と pb.multiChildType から自動生成する。</summary>
        public List<List<string>> springs;
        /// <summary>null なら HarnessCase.colliders から暫定変換する。</summary>
        public List<VrmColliderSpec> colliders;

        /// <summary>浅いコピー。</summary>
        public VrmSpec Clone() => (VrmSpec)MemberwiseClone();
    }

    public sealed class ColliderSpec
    {
        /// <summary>World(=Base) | Driver | ノード名</summary>
        public string attach = "Driver";
        /// <summary>Sphere | Capsule | Plane</summary>
        public string shape = "Sphere";
        public float radius = 0.05f;
        public float height = 0.2f;
        public float[] position = { 0f, 0f, 0f };
        public float[] rotationEuler = { 0f, 0f, 0f };
        public bool insideBounds;
        public bool bonesAsSpheres;
    }

    public sealed class VrmColliderSpec
    {
        public string attach = "Driver";
        /// <summary>Sphere | Capsule | Plane | SphereInside | CapsuleInside</summary>
        public string type = "Sphere";
        public float radius = 0.05f;
        public float[] offset = { 0f, 0f, 0f };
        public float[] tail = { 0f, 0f, 0f };
        public float[] normal = { 0f, 1f, 0f };
    }

    public sealed class MotionSpec
    {
        /// <summary>
        /// step     : t >= start で position / euler だけ変位する
        /// sinePos  : axis * amplitude * sin(2π f t)
        /// sineRot  : axis 周りに amplitude[deg] * sin(2π f t)
        /// chirpPos : sinePos の周波数を frequency → frequencyEnd へ length 秒で線形掃引
        /// chirpRot : sineRot の掃引版
        /// rampRot  : start から length 秒かけて axis 周りに amplitude[deg] まで smoothstep で回転して保持
        /// rampPos  : start から length 秒かけて axis * amplitude まで smoothstep で移動して保持
        /// </summary>
        public string type = "step";
        /// <summary>motion 開始 (warmup 終了) からの開始時刻 [s]。</summary>
        public float start = 0f;
        /// <summary>chirp / ramp の長さ [s]。</summary>
        public float length = 1f;
        public float[] axis = { 1f, 0f, 0f };
        public float amplitude = 0.1f;
        public float frequency = 1f;
        public float frequencyEnd = 4f;
        public float[] position = { 0f, 0f, 0f };
        public float[] euler = { 0f, 0f, 0f };
    }
}
