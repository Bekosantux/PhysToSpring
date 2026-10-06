using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UniVRM10;

namespace Bekosan.PhysToSpring.Editor
{
    /// <summary>
    /// 変換ウィンドウ。対象を事前チェックして警告などを一覧し、ボタンで変換する。
    /// 一覧は Console へのログの代わりで、変換後は変換結果を表示する。
    /// </summary>
    public sealed class PhysToSpringWindow : EditorWindow
    {
        enum ConvertMode
        {
            Copy,
            InPlace,
        }

        static readonly string[] ModeLabels = { "複製して変換", "そのまま変換" };
        static readonly string[] ModeNotes =
        {
            "複製を作って変換します。",
            "対象を直接変換します。既存の VRM SpringBone 設定は置き換えられます。",
        };

        [SerializeField] GameObject target;
        [SerializeField] ConvertMode mode;
        [SerializeField] bool hideSource = true;
        [SerializeField] bool removePhysBones = true;
        [SerializeField] bool createVrmObject = true;
        [SerializeField] bool showInfo = true;
        [SerializeField] bool showWarning = true;
        [SerializeField] bool showError = true;

        ConversionReport report = new ConversionReport();
        /// <summary>true なら report は変換結果。再チェック・対象の変更・Undo までチェックで上書きしない。</summary>
        bool showingResult;
        bool converted;
        bool dirty;
        Vector2 scroll;
        GUIStyle messageStyle;

        [MenuItem("Tools/PhysToSpring")]
        static void Open()
        {
            var window = GetWindow<PhysToSpringWindow>("PhysToSpring");
            window.minSize = new Vector2(360f, 300f);
            var go = Selection.activeGameObject;
            if (go != null && IsSceneObject(go)) window.SetTarget(go);
        }

        static bool IsSceneObject(GameObject go) => go.scene.IsValid() && !EditorUtility.IsPersistent(go);

        void OnEnable()
        {
            ObjectChangeEvents.changesPublished += OnChangesPublished;
            Undo.undoRedoPerformed += OnUndoRedo;
            Check();
        }

        void OnDisable()
        {
            ObjectChangeEvents.changesPublished -= OnChangesPublished;
            Undo.undoRedoPerformed -= OnUndoRedo;
        }

        void OnChangesPublished(ref ObjectChangeEventStream stream) => dirty = true;

        void OnUndoRedo()
        {
            showingResult = false;
            dirty = true;
        }

        void OnFocus() => dirty = true;

        void OnInspectorUpdate()
        {
            if (!dirty || showingResult) return;
            Check();
            Repaint();
        }

        void SetTarget(GameObject go)
        {
            target = go;
            Check();
        }

        void Check()
        {
            dirty = false;
            showingResult = false;
            report = new ConversionReport();
            if (target == null) return;
            if (!IsSceneObject(target))
            {
                report.Error("シーン上のオブジェクトを指定してください (プレハブアセットは変換できません)", target);
                return;
            }
            try
            {
                var plans = PhysToSpringConverter.Plan(target.transform, report);
                CheckTarget(target, report);
                if (!report.HasError) report.Info($"{plans.Count} 本の spring に変換します", target);
            }
            catch (Exception e)
            {
                report.Error("チェック中に予期しないエラーが発生しました: " + e.Message, target);
                Debug.LogException(e);
            }
        }

        /// <summary>PhysBone 以外の、変換先アバターとしての確認。</summary>
        static void CheckTarget(GameObject go, ConversionReport report)
        {
            var animator = go.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
            {
                report.Warn($"{go.name}: Humanoid の Animator がありません。VRM としてエクスポートするには Humanoid のアバタールートを指定してください", go);
            }
            var instance = go.GetComponent<Vrm10Instance>();
            if (instance != null && (instance.SpringBone.Springs.Count > 0 || instance.SpringBone.ColliderGroups.Count > 0))
            {
                report.Warn($"{go.name}: 既存の VRM SpringBone 設定 (spring {instance.SpringBone.Springs.Count} 本) は置き換えられます", instance);
            }
        }

        void Convert()
        {
            if (mode == ConvertMode.InPlace && !EditorUtility.DisplayDialog("PhysToSpring",
                    $"{target.name} の既存の VRM SpringBone 設定 (joint / collider) を置き換えます。" +
                    (removePhysBones ? "PhysBone とそのコライダーも削除します。" : ""), "変換", "キャンセル"))
            {
                return;
            }
            var options = new ConversionOptions
            {
                HideSource = hideSource,
                RemovePhysBones = removePhysBones,
                CreateVrmObject = createVrmObject,
            };
            var result = new ConversionReport();
            try
            {
                if (mode == ConvertMode.Copy)
                {
                    var copy = PhysToSpringConverter.ConvertCopy(target, result, options);
                    converted = copy != null;
                    if (converted)
                    {
                        target = copy;
                        Selection.activeGameObject = copy;
                    }
                }
                else
                {
                    converted = PhysToSpringConverter.ConvertInPlace(target, result, options);
                }
            }
            catch (Exception e)
            {
                converted = false;
                result.Error("変換中に予期しないエラーが発生しました: " + e.Message + " (途中まで変更されている場合は Ctrl+Z で元に戻してください)", target);
                Debug.LogException(e);
            }
            report = result;
            showingResult = true;
            scroll = Vector2.zero;
        }

        void OnGUI()
        {
            messageStyle ??= new GUIStyle(EditorStyles.label) { wordWrap = true };

            EditorGUILayout.Space();
            using (var change = new EditorGUI.ChangeCheckScope())
            {
                var go = (GameObject)EditorGUILayout.ObjectField("アバター", target, typeof(GameObject), true);
                if (change.changed) SetTarget(go);
            }
            mode = (ConvertMode)EditorGUILayout.Popup("変換方法", (int)mode, ModeLabels);
            EditorGUILayout.LabelField(" ", ModeNotes[(int)mode], EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(mode != ConvertMode.Copy))
            {
                hideSource = EditorGUILayout.Toggle(new GUIContent("元のアバターを非表示", "複製して変換するとき、元のアバターを非表示にします"), hideSource);
            }
            removePhysBones = EditorGUILayout.Toggle(new GUIContent("PhysBone を削除", "残すと再生時に PhysBone と SpringBone が二重に動きます"), removePhysBones);
            createVrmObject = EditorGUILayout.Toggle(new GUIContent("VRM10Object を作成",
                $"Vrm10Instance に VRM10Object が無ければ {ConversionOptions.DefaultGeneratedFolder} に作ります。無いと再生時に SpringBone が動きません"), createVrmObject);

            EditorGUILayout.Space();
            DrawStatus();
            DrawToolbar();
            DrawEntries();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(target == null || showingResult || report.HasError))
            {
                if (GUILayout.Button("変換", GUILayout.Height(30f))) Convert();
            }
            EditorGUILayout.Space();
        }

        void DrawStatus()
        {
            if (target == null)
            {
                EditorGUILayout.HelpBox("変換するアバターのルートを指定してください。", MessageType.Info);
            }
            else if (showingResult)
            {
                EditorGUILayout.HelpBox(converted
                        ? "変換しました。下の一覧は変換結果です (再チェックでチェック結果に戻ります)。"
                        : "変換できませんでした。エラーを確認してください。",
                    converted ? MessageType.Info : MessageType.Error);
            }
            else if (report.HasError)
            {
                EditorGUILayout.HelpBox("エラーを修正するまで変換できません。", MessageType.Error);
            }
            else
            {
                var warnings = Count(ReportLevel.Warning);
                EditorGUILayout.HelpBox(warnings > 0 ? $"変換できます (警告 {warnings} 件)。" : "変換できます。",
                    warnings > 0 ? MessageType.Warning : MessageType.Info);
            }
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("再チェック", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false))) Check();
                if (GUILayout.Button("コピー", EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                {
                    EditorGUIUtility.systemCopyBuffer = ToText();
                }
                GUILayout.FlexibleSpace();
                showInfo = LevelToggle(showInfo, ReportLevel.Info);
                showWarning = LevelToggle(showWarning, ReportLevel.Warning);
                showError = LevelToggle(showError, ReportLevel.Error);
            }
        }

        bool LevelToggle(bool value, ReportLevel level)
        {
            var content = new GUIContent(Count(level).ToString(), Icon(level));
            return GUILayout.Toggle(value, content, EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
        }

        void DrawEntries()
        {
            const float iconSize = 16f;
            const float pad = 4f;
            // 折り返し高さの見積もり用。余白とスクロールバーの分だけ実際より狭く見積もる
            var width = position.width - 40f;
            var e = Event.current;
            var row = 0;
            using (var view = new EditorGUILayout.ScrollViewScope(scroll, EditorStyles.helpBox, GUILayout.ExpandHeight(true)))
            {
                scroll = view.scrollPosition;
                // エラー → 警告 → 情報。同じレベル内は出力順
                foreach (var entry in report.Entries.OrderByDescending(x => x.Level))
                {
                    if (!IsShown(entry.Level)) continue;
                    var content = new GUIContent(entry.Message, entry.Context != null ? "クリックで対象を表示 / ダブルクリックで選択" : null);
                    var height = Mathf.Max(iconSize, messageStyle.CalcHeight(content, width - iconSize - pad * 2)) + pad;
                    var rect = GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));
                    if (e.type == EventType.Repaint && row++ % 2 == 1)
                    {
                        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.08f));
                    }
                    GUI.DrawTexture(new Rect(rect.x + pad, rect.y + pad * 0.5f, iconSize, iconSize), Icon(entry.Level));
                    GUI.Label(new Rect(rect.x + iconSize + pad * 2, rect.y + pad * 0.5f, rect.width - iconSize - pad * 2, rect.height - pad), content, messageStyle);
                    if (entry.Context != null && e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
                    {
                        if (e.clickCount == 2) Selection.activeObject = entry.Context;
                        else EditorGUIUtility.PingObject(entry.Context);
                        e.Use();
                    }
                }
            }
        }

        bool IsShown(ReportLevel level)
        {
            switch (level)
            {
                case ReportLevel.Error: return showError;
                case ReportLevel.Warning: return showWarning;
                default: return showInfo;
            }
        }

        int Count(ReportLevel level) => report.Entries.Count(x => x.Level == level);

        static Texture Icon(ReportLevel level)
        {
            switch (level)
            {
                case ReportLevel.Error: return EditorGUIUtility.IconContent("console.erroricon.sml").image;
                case ReportLevel.Warning: return EditorGUIUtility.IconContent("console.warnicon.sml").image;
                default: return EditorGUIUtility.IconContent("console.infoicon.sml").image;
            }
        }

        string ToText()
        {
            var sb = new StringBuilder();
            foreach (var entry in report.Entries.OrderByDescending(x => x.Level))
            {
                sb.Append('[').Append(entry.Level).Append("] ").AppendLine(entry.Message);
            }
            return sb.ToString();
        }
    }
}
