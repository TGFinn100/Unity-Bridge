using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityBridge.Editor
{
    // Generic read/write over the jsonl-per-record-type files (LOCKED: one
    // JSON object per line via MiniJson, no SQLite). Writes are atomic
    // (temp file + replace) for the same reason BridgeServer's port file is:
    // a write torn by a mid-save crash or reload must never leave a corrupt
    // file behind for the next Load() to choke on.
    internal static class JsonLinesIO
    {
        // Encoding.UTF8 writes a byte-order mark; File.WriteAllText's
        // encoding-less overload doesn't, which is what meta.json and the
        // port file already rely on. Use the same no-BOM encoding here so
        // every file under Library/UnityBridge/ is consistently plain UTF-8
        // — a leading BOM is invisible in most editors but breaks a naive
        // grep/diff on the first line, undermining the LOCKED
        // "grep/diff-friendly" design goal.
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static List<T> ReadLines<T>(string path, Func<Dictionary<string, object>, T> fromDict)
        {
            var result = new List<T>();
            if (!File.Exists(path)) return result;

            foreach (string line in File.ReadAllLines(path, Utf8NoBom))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var dict = (Dictionary<string, object>)MiniJson.Parse(line);
                result.Add(fromDict(dict));
            }
            return result;
        }

        internal static void WriteLines<T>(string path, IEnumerable<T> items, Func<T, Dictionary<string, object>> toDict)
        {
            var sb = new StringBuilder();
            foreach (var item in items)
            {
                sb.Append(MiniJson.Write(toDict(item)));
                sb.Append('\n');
            }

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, sb.ToString(), Utf8NoBom);
            if (File.Exists(path)) File.Replace(tempPath, path, null);
            else File.Move(tempPath, path);
        }
    }
}
