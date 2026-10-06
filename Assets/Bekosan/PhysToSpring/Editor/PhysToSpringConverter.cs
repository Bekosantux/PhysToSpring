using System.IO;
using UnityEditor;
using UnityEngine;
using UniVRM10;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Bekosan.PhysToSpring.Editor
{
    /// <summary>
    /// アバター配下の VRCPhysBone を VRM 1.0 SpringBone (Vrm10Instance) に変換する。
    /// ウィンドウ (<see cref="PhysToSpringWindow"/>) からの変換では、VRM10Object の作成と PhysBone / PhysBone コライダーの削除まで行う。
    /// </summary>
    public static class PhysToSpringConverter
    {
        const string GeneratedFolder = "Assets/PhysToSpring_Generated";
        const string DoneNote = "エクスポート前に Vrm10Instance の VRM10Object (" + GeneratedFolder + ") に作者などのメタ情報を入力してください。";

        /// <summary>変換して Vrm10Instance を返す。エラーがあればシーンを変更せず null。</summary>
        public static Vrm10Instance Convert(Transform avatarRoot, ConversionReport report)
        {
            var plans = PhysBoneReader.Read(avatarRoot, report);
            if (report.HasError) return null;
            var instance = SpringBoneWriter.Write(avatarRoot, plans, report);
            report.Info($"{avatarRoot.name}: {plans.Count} 本の spring を作成しました", instance);
            return instance;
        }

        /// <summary>
        /// Vrm10Instance に VRM10Object が無ければ作る。無いと再生時に Vrm10Instance が無効になり
        /// (no VRM10Object)、SpringBone が動かない。エクスポートのメタ情報もここに入る。
        /// </summary>
        public static void EnsureVrmObject(Vrm10Instance instance, ConversionReport report)
        {
            if (instance.Vrm != null) return;
            if (!AssetDatabase.IsValidFolder(GeneratedFolder))
            {
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(GeneratedFolder));
            }
            var fileName = instance.name;
            foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            var path = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedFolder}/{fileName}.asset");

            var asset = ScriptableObject.CreateInstance<VRM10Object>();
            asset.Meta.Name = instance.name;
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            Undo.RecordObject(instance, "PhysToSpring");
            instance.Vrm = asset;
            report.Info($"VRM10Object を作成しました: {path} (エクスポート前に作者などのメタ情報を入力してください)", asset);
        }

        /// <summary>変換後に残すと PhysBone と SpringBone が二重に動くので、PhysBone とそのコライダーを削除する。</summary>
        public static void RemovePhysBones(Transform avatarRoot, ConversionReport report)
        {
            var physBones = avatarRoot.GetComponentsInChildren<VRCPhysBone>(true);
            var colliders = avatarRoot.GetComponentsInChildren<VRCPhysBoneCollider>(true);
            foreach (var c in physBones) Undo.DestroyObjectImmediate(c);
            foreach (var c in colliders) Undo.DestroyObjectImmediate(c);
            report.Info($"{avatarRoot.name}: PhysBone {physBones.Length} 個とコライダー {colliders.Length} 個を削除しました", avatarRoot);
        }

        /// <summary>ウィンドウ用: 変換し、VRM10Object を用意して PhysBone を削除する。</summary>
        static Vrm10Instance ConvertAndCleanUp(Transform avatarRoot, ConversionReport report)
        {
            var instance = Convert(avatarRoot, report);
            if (instance == null) return null;
            EnsureVrmObject(instance, report);
            RemovePhysBones(avatarRoot, report);
            return instance;
        }

        /// <summary>
        /// 複製を変換し、元のアバターは非表示にする。失敗したら複製ごと元に戻して null。
        /// </summary>
        public static GameObject ConvertCopy(GameObject source, ConversionReport report)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var copy = Object.Instantiate(source, source.transform.parent);
            copy.name = source.name + " (VRM)";
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            Undo.RegisterCreatedObjectUndo(copy, "PhysToSpring");
            if (ConvertAndCleanUp(copy.transform, report) == null)
            {
                Undo.RevertAllDownToGroup(group);
                return null;
            }
            Undo.RecordObject(source, "PhysToSpring");
            source.SetActive(false);
            report.Info($"{copy.name} を作成しました。元のアバターは非表示にしました", copy);
            report.Info(DoneNote, copy);
            Undo.SetCurrentGroupName("PhysToSpring");
            Undo.CollapseUndoOperations(group);
            return copy;
        }

        /// <summary>
        /// そのまま変換する。既存の VRM SpringBone 設定は置き換わる。エラーがあればシーンを変更せず false。
        /// </summary>
        public static bool ConvertInPlace(GameObject target, ConversionReport report)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            if (ConvertAndCleanUp(target.transform, report) == null) return false;
            report.Info(DoneNote, target);
            Undo.SetCurrentGroupName("PhysToSpring");
            Undo.CollapseUndoOperations(group);
            return true;
        }
    }
}
