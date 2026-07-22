using System.Collections.Generic;
using System.Globalization;

namespace UnityBridge.Editor
{
    // Record shapes for Library/UnityBridge/*.jsonl (LOCKED schema, task brief
    // "Index store" section). Each ToDict/FromDict pair round-trips through
    // MiniJson so the on-disk files stay the same compact-JSON-per-line shape
    // as HTTP responses.
    internal sealed class AssetRecord
    {
        public string Guid;
        public string Path;
        public string Type;
        public string Name;
        public long SizeBytes;
        public string Mtime;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "guid", Guid },
            { "path", Path },
            { "type", Type },
            { "name", Name },
            { "sizeBytes", SizeBytes },
            { "mtime", Mtime }
        };

        public static AssetRecord FromDict(Dictionary<string, object> d) => new AssetRecord
        {
            Guid = (string)d["guid"],
            Path = (string)d["path"],
            Type = (string)d["type"],
            Name = (string)d["name"],
            SizeBytes = JsonNum.ToLong(d["sizeBytes"]),
            Mtime = (string)d["mtime"]
        };
    }

    // Shared shape for prefab_components.jsonl and scene_components.jsonl —
    // both are "{ guid, componentType, objectPath }" per the LOCKED schema,
    // just sourced differently (PrefabScanner vs SceneYamlParser).
    internal sealed class ComponentRecord
    {
        public string Guid;
        public string ComponentType;
        public string ObjectPath;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "guid", Guid },
            { "componentType", ComponentType },
            { "objectPath", ObjectPath }
        };

        public static ComponentRecord FromDict(Dictionary<string, object> d) => new ComponentRecord
        {
            Guid = (string)d["guid"],
            ComponentType = (string)d["componentType"],
            ObjectPath = (string)d["objectPath"]
        };
    }

    internal sealed class ScenePrefabInstanceRecord
    {
        public string Guid; // the scene asset's own guid
        public string SourcePrefabGuid;
        public string ObjectPath;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "guid", Guid },
            { "sourcePrefabGuid", SourcePrefabGuid },
            { "objectPath", ObjectPath }
        };

        public static ScenePrefabInstanceRecord FromDict(Dictionary<string, object> d) => new ScenePrefabInstanceRecord
        {
            Guid = (string)d["guid"],
            SourcePrefabGuid = (string)d["sourcePrefabGuid"],
            ObjectPath = (string)d["objectPath"]
        };
    }

    internal sealed class ScriptTypeRecord
    {
        public string Guid;
        public string ClassName;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "guid", Guid },
            { "className", ClassName }
        };

        public static ScriptTypeRecord FromDict(Dictionary<string, object> d) => new ScriptTypeRecord
        {
            Guid = (string)d["guid"],
            ClassName = (string)d["className"]
        };
    }

    internal sealed class IndexMeta
    {
        public int SchemaVersion;
        public string LastFullScan;

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "schemaVersion", SchemaVersion },
            { "lastFullScan", LastFullScan }
        };

        public static IndexMeta FromDict(Dictionary<string, object> d) => new IndexMeta
        {
            SchemaVersion = (int)JsonNum.ToLong(d["schemaVersion"]),
            LastFullScan = (string)d["lastFullScan"]
        };
    }

    // MiniJson.Parse hands back int/long/double depending on magnitude —
    // record fields that are logically integers (sizeBytes, schemaVersion)
    // need one coercion path regardless of which numeric type came back.
    internal static class JsonNum
    {
        internal static long ToLong(object value)
        {
            switch (value)
            {
                case long l: return l;
                case int i: return i;
                case double d: return (long)d;
                case string s: return long.Parse(s, CultureInfo.InvariantCulture);
                default: return 0;
            }
        }

        // v2: float-valued fields (position/rotation/scale components,
        // Float-type set-field writes) need the same any-numeric-type
        // coercion as ToLong above, just without truncating to an integer.
        internal static double ToDouble(object value)
        {
            switch (value)
            {
                case double d: return d;
                case float f: return f;
                case long l: return l;
                case int i: return i;
                case string s: return double.Parse(s, CultureInfo.InvariantCulture);
                default: return 0;
            }
        }
    }
}
