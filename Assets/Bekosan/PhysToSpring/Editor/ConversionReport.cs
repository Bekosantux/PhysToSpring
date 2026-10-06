using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Bekosan.PhysToSpring.Editor
{
    public enum ReportLevel
    {
        Info,
        Warning,
        Error,
    }

    public sealed class ConversionReport
    {
        public readonly struct Entry
        {
            public readonly ReportLevel Level;
            public readonly string Message;
            public readonly Object Context;

            public Entry(ReportLevel level, string message, Object context)
            {
                Level = level;
                Message = message;
                Context = context;
            }
        }

        public readonly List<Entry> Entries = new List<Entry>();

        public bool HasError => Entries.Any(e => e.Level == ReportLevel.Error);

        public void Info(string message, Object context = null) => Entries.Add(new Entry(ReportLevel.Info, message, context));
        public void Warn(string message, Object context = null) => Entries.Add(new Entry(ReportLevel.Warning, message, context));
        public void Error(string message, Object context = null) => Entries.Add(new Entry(ReportLevel.Error, message, context));

        public void Log()
        {
            foreach (var e in Entries)
            {
                var msg = "[PhysToSpring] " + e.Message;
                switch (e.Level)
                {
                    case ReportLevel.Error: Debug.LogError(msg, e.Context); break;
                    case ReportLevel.Warning: Debug.LogWarning(msg, e.Context); break;
                    default: Debug.Log(msg, e.Context); break;
                }
            }
        }

        /// <summary>ダイアログ用の要約 (エラーと警告を先頭から max 件)。</summary>
        public string Summary(int max = 12)
        {
            var sb = new StringBuilder();
            var shown = Entries.Where(e => e.Level != ReportLevel.Info).OrderByDescending(e => e.Level).ToList();
            foreach (var e in shown.Take(max))
            {
                sb.Append(e.Level == ReportLevel.Error ? "エラー: " : "警告: ").AppendLine(e.Message);
            }
            if (shown.Count > max) sb.AppendLine($"... ほか {shown.Count - max} 件 (Console を参照)");
            return sb.ToString();
        }
    }
}
