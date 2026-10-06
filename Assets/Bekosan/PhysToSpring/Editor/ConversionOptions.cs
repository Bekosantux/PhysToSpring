namespace Bekosan.PhysToSpring.Editor
{
    /// <summary><see cref="PhysToSpringConverter.ConvertCopy"/> / <see cref="PhysToSpringConverter.ConvertInPlace"/> の後処理の設定。</summary>
    public sealed class ConversionOptions
    {
        public const string DefaultGeneratedFolder = "Assets/PhysToSpring_Generated";

        /// <summary>ConvertCopy で元のアバターを非表示にする。</summary>
        public bool HideSource = true;

        /// <summary>変換後に VRCPhysBone / VRCPhysBoneCollider を削除する。残すと再生時に PhysBone と SpringBone が二重に動く。</summary>
        public bool RemovePhysBones = true;

        /// <summary>Vrm10Instance に VRM10Object が無ければ <see cref="GeneratedFolder"/> に作る。無いと再生時に Vrm10Instance が無効になり SpringBone が動かない。</summary>
        public bool CreateVrmObject = true;

        /// <summary>VRM10Object の作成先。Assets 以下のフォルダ。無ければ作る。</summary>
        public string GeneratedFolder = DefaultGeneratedFolder;
    }
}
