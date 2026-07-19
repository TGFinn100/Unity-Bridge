using System.Collections.Generic;

namespace UnityBridge.Editor
{
    // Manifest entry shape for Library/UnityBridge/log_watches/manifest.json
    // (LOCKED, v1.6 task brief "On-disk layout"). Mirrors IndexRecords.cs's
    // ToDict/FromDict round-trip style so manifest content and HTTP response
    // content (GET /logs/watches) come from the same dict, never two
    // hand-maintained shapes.
    internal sealed class LogWatchDefinition
    {
        public string Name;
        public string Pattern;
        public int Capacity;
        public string CreatedAt;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "name", Name },
            { "pattern", Pattern },
            { "capacity", Capacity },
            { "createdAt", CreatedAt }
        };

        public static LogWatchDefinition FromDict(Dictionary<string, object> d) => new LogWatchDefinition
        {
            Name = (string)d["name"],
            Pattern = (string)d["pattern"],
            Capacity = (int)JsonNum.ToLong(d["capacity"]),
            CreatedAt = (string)d["createdAt"]
        };
    }

    // One condensed record in Library/UnityBridge/log_watches/<name>.jsonl
    // (LOCKED shape): named capture groups from a matched log message, plus
    // when it matched. Values are always strings (Regex.Group.Value), never
    // coerced to number/bool — the brief's extraction format is regex
    // capture only, no typed schema.
    internal sealed class LogWatchRecord
    {
        public Dictionary<string, object> Values;
        public string Time;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "values", Values },
            { "time", Time }
        };

        public static LogWatchRecord FromDict(Dictionary<string, object> d) => new LogWatchRecord
        {
            Values = (Dictionary<string, object>)d["values"],
            Time = (string)d["time"]
        };
    }
}
