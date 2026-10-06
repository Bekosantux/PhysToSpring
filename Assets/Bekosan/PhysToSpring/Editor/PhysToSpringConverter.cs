using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UniVRM10;
using VRC.SDK3.Dynamics.PhysBone.Components;

namespace Bekosan.PhysToSpring.Editor
{
    /// <summary>
    /// アバター配下の VRCPhysBone を VRM 1.0 SpringBone (Vrm10Instance) に変換する。
    /// ConvertCopy / ConvertInPlace は <see cref="ConversionOptions"/> に従って VRM10Object の作成や PhysBone の削除まで行う。
    /// </summary>
    public static class PhysToSpringConverter
    {
        const string DoneNote = "エクスポート前に Vrm10Instance の VRM10Object に作者などのメタ情報を入力してください。";

        /// <summary>
        /// シーンを変更せずに変換計画だけを作る。書き込みを自前で行う場合に使う。
        /// report.HasError なら計画は不完全なので使わないこと。
        /// </summary>
        public static List<SpringPlan> Plan(Transform avatarRoot, ConversionReport report)
        {
            return PhysBoneReader.Read(avatarRoot, report);
        }

        /// <summary>変換して Vrm10Instance を返す。エラーがあればシーンを変更せず null。後処理は行わない。</summary>
        public static Vrm10Instance Convert(Transform avatarRoot, ConversionReport report)
        {
            var plans = Plan(avatarRoot, report);
            if (report.HasError) return null;
            var instance = SpringBoneWriter.Write(avatarRoot, plans, report);
            report.Info($"{avatarRoot.name}: {plans.Count} 本の spring を作成しました", instance);
            return instance;
        }

        /// <summary>
        /// Vrm10Instance に VRM10Object が無ければ folder に作る。無いと再生時に Vrm10Instance が無効になり
        /// (no VRM10Object)、SpringBone が動かない。エクスポートのメタ情報もここに入る。
        /// </summary>
        public static void EnsureVrmObject(Vrm10Instance instance, ConversionReport report, string folder = ConversionOptions.DefaultGeneratedFolder)
        {
            if (instance.Vrm != null) return;
            folder = NormalizeFolder(folder);
            EnsureFolder(folder);
            var fileName = instance.name;
            foreach (var c in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}.asset");

            var asset = ScriptableObject.CreateInstance<VRM10Object>();
            asset.Meta.Name = instance.name;
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();

            Undo.RecordObject(instance, "PhysToSpring");
            instance.Vrm = asset;
            report.Info($"VRM10Object を作成しました: {path}", asset);
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

        /// <summary>
        /// 複製を変換する。失敗したら複製ごと元に戻して null。
        /// </summary>
        public static GameObject ConvertCopy(GameObject source, ConversionReport report, ConversionOptions options = null)
        {
            options ??= new ConversionOptions();
            if (!Validate(options, report)) return null;
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var copy = Object.Instantiate(source, source.transform.parent);
            copy.name = source.name + " (VRM)";
            copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            Undo.RegisterCreatedObjectUndo(copy, "PhysToSpring");
            if (ConvertAndCleanUp(copy.transform, report, options) == null)
            {
                Undo.RevertAllDownToGroup(group);
                return null;
            }
            if (options.HideSource)
            {
                Undo.RecordObject(source, "PhysToSpring");
                source.SetActive(false);
                report.Info($"{copy.name} を作成しました。元のアバターは非表示にしました", copy);
            }
            else
            {
                report.Info($"{copy.name} を作成しました", copy);
            }
            Undo.SetCurrentGroupName("PhysToSpring");
            Undo.CollapseUndoOperations(group);
            return copy;
        }

        /// <summary>
        /// そのまま変換する。既存の VRM SpringBone 設定は置き換わる。エラーがあればシーンを変更せず false。
        /// </summary>
        public static bool ConvertInPlace(GameObject target, ConversionReport report, ConversionOptions options = null)
        {
            options ??= new ConversionOptions();
            if (!Validate(options, report)) return false;
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            if (ConvertAndCleanUp(target.transform, report, options) == null) return false;
            Undo.SetCurrentGroupName("PhysToSpring");
            Undo.CollapseUndoOperations(group);
            return true;
        }

        /// <summary>変換し、options に従って VRM10Object を用意して PhysBone を削除する。</summary>
        static Vrm10Instance ConvertAndCleanUp(Transform avatarRoot, ConversionReport report, ConversionOptions options)
        {
            var instance = Convert(avatarRoot, report);
            if (instance == null) return null;
            if (options.CreateVrmObject) EnsureVrmObject(instance, report, options.GeneratedFolder);
            if (instance.Vrm != null)
            {
                report.Info(DoneNote, instance.Vrm);
            }
            else
            {
                report.Warn($"{avatarRoot.name}: Vrm10Instance に VRM10Object が設定されていません。このままでは再生時に SpringBone が動きません", instance);
            }
            if (options.RemovePhysBones)
            {
                RemovePhysBones(avatarRoot, report);
            }
            else
            {
                report.Warn($"{avatarRoot.name}: PhysBone を残しました。再生時は PhysBone と SpringBone が二重に動きます", avatarRoot);
            }
            return instance;
        }

        static bool Validate(ConversionOptions options, ConversionReport report)
        {
            if (!options.CreateVrmObject) return true;
            var folder = NormalizeFolder(options.GeneratedFolder);
            if (folder == "Assets" || folder.StartsWith("Assets/")) return true;
            report.Error($"VRM10Object の作成先 ({options.GeneratedFolder}) は Assets 以下のフォルダを指定してください");
            return false;
        }

        static string NormalizeFolder(string folder) => (folder ?? "").Replace('\\', '/').TrimEnd('/');

        static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var slash = folder.LastIndexOf('/');
            if (slash < 0) throw new System.ArgumentException($"Assets 以下のフォルダではありません: {folder}");
            var parent = folder.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, folder.Substring(slash + 1));
        }
    }
}
