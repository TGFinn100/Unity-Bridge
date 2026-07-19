using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    // Named, condensed log watches (v1.6 LOCKED task brief). A watch pairs a
    // regex-with-named-groups against every incoming Console message; a
    // match is condensed into that watch's own bounded ring buffer instead
    // of consuming shared LogBuffer capacity. One in-process lock guards all
    // watch state — matches happen on whatever thread logged
    // (Application.logMessageReceivedThreaded), the same discipline
    // IndexStore already uses for its own cross-thread lock.
    internal static class LogWatchStore
    {
        sealed class WatchEntry
        {
            public LogWatchDefinition Definition;
            public Regex Regex;
            public Queue<LogWatchRecord> Records;
            public bool Dirty;
        }

        static readonly object _gate = new object();
        static readonly Dictionary<string, WatchEntry> _watches = new Dictionary<string, WatchEntry>(StringComparer.Ordinal);
        static double _lastDirtyTime = -1;

        const double DebounceSeconds = 0.25; // matches BridgeAssetPostprocessor's existing debounce window

        static string WatchesDir => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "log_watches");
        static string ManifestPath => Path.Combine(WatchesDir, "manifest.json");
        static string RecordsPath(string name) => Path.Combine(WatchesDir, name + ".jsonl");

        // Pure file I/O — safe to call from BridgeServer's static
        // constructor before any main-thread tick has run, same as
        // IndexStore.Load()/ActionToken.EnsureExists(). Checks a
        // pre-computed entering-Play-Mode flag (see LogPersistenceLifecycle
        // and LogBuffer.Load()'s comment on why this is passed in rather
        // than consumed here directly) rather than checking
        // EditorApplication.isPlaying — the brief's original mechanism,
        // disproved by live testing (isPlaying doesn't read back true at
        // this point during an entry-triggered reload, making it
        // indistinguishable from a plain recompile). True means every
        // watch's own ACCUMULATED RECORDS are wiped (fresh start for the
        // data), but each watch's DEFINITION (name/pattern/capacity)
        // survives and stays active.
        //
        // A second real bug caught during v1.6 acceptance testing
        // 2026-07-19: the original version of this method deleted the
        // *entire* log_watches/ directory on entry, including
        // manifest.json — meaning a watch registered right before entering
        // Play Mode (exactly the brief's own documented idiom: "register a
        // watch... -> drive the behavior via /act/playmode/enter -> GET
        // /logs/watches to confirm it's active") vanished the instant Play
        // Mode started, making it useless for observing that very run. The
        // brief's actual wording only ever says persisted *log data* is
        // wiped at entry, never that a watch's own registration is —
        // confirmed by its own skill-file note ("don't expect a watch
        // registered before a test run to still hold data from a *previous*
        // run," which presupposes the watch itself survives). Fixed:
        // definitions are always loaded from manifest.json regardless of
        // enteringPlayMode; only each watch's records (in-memory queue, and
        // its on-disk .jsonl, rewritten empty) are cleared when entering.
        internal static void Load(bool enteringPlayMode)
        {
            try
            {
                if (!File.Exists(ManifestPath)) return; // nothing persisted yet — start empty, same as IndexStore's missing-index case

                var manifest = MiniJson.Parse(File.ReadAllText(ManifestPath)) as Dictionary<string, object>;
                var rawList = manifest != null && manifest.TryGetValue("watches", out var w) ? w as List<object> : null;
                if (rawList == null) return;

                foreach (var item in rawList)
                {
                    try
                    {
                        var def = LogWatchDefinition.FromDict((Dictionary<string, object>)item);
                        var regex = new Regex(def.Pattern);
                        var queue = enteringPlayMode
                            ? new Queue<LogWatchRecord>()
                            : new Queue<LogWatchRecord>(JsonLinesIO.ReadLines(RecordsPath(def.Name), LogWatchRecord.FromDict));

                        lock (_gate)
                        {
                            _watches[def.Name] = new WatchEntry { Definition = def, Regex = regex, Records = queue };
                        }

                        // Rewrite the on-disk records file empty too, so a
                        // crash right after this reload can't resurrect the
                        // pre-entry data on a later self-heal load.
                        if (enteringPlayMode) PersistRecords(def.Name);
                    }
                    catch (Exception ex)
                    {
                        // Corrupt manifest entry or unparsable regex/jsonl —
                        // skip just that watch (self-heal, same spirit as
                        // IndexStore.Load()'s corrupt-index handling) rather
                        // than losing every other watch over one bad entry.
                        Debug.LogWarning($"[UnityBridge] Skipping corrupt log watch entry: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UnityBridge] Log watch manifest load failed, starting empty: {ex.Message}");
            }
        }

        // --- Read-only queries (thread-safe, any thread) ---

        internal static bool Exists(string name)
        {
            lock (_gate) return _watches.ContainsKey(name);
        }

        internal static List<LogWatchDefinition> ListActive()
        {
            lock (_gate) return _watches.Values.Select(w => w.Definition).OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        }

        // Newest-first, same ordering convention as LogsTailEndpoint.
        internal static (List<LogWatchRecord> items, int total, string pattern, int capacity, string createdAt) GetRecords(string name)
        {
            lock (_gate)
            {
                if (!_watches.TryGetValue(name, out var entry)) return (null, 0, null, 0, null);
                var items = entry.Records.Reverse().ToList();
                return (items, items.Count, entry.Definition.Pattern, entry.Definition.Capacity, entry.Definition.CreatedAt);
            }
        }

        // Throws (ArgumentException / RegexParseException) on an invalid
        // pattern — callers turn that into 400 invalid_pattern (LOCKED,
        // "validated at registration time so a bad pattern never silently
        // matches nothing forever"). Pure regex compilation, no Unity API,
        // safe on any thread.
        internal static void CompileValidate(string pattern) => _ = new Regex(pattern);

        // --- Mutations (main-thread only, deferred via ActionScheduler's
        // MainThreadAction — same discipline as every other Library/
        // UnityBridge/ writer in this codebase, even though this particular
        // I/O has no Unity-API dependency) ---

        internal static void Register(string name, string pattern, int capacity, string createdAt)
        {
            var def = new LogWatchDefinition { Name = name, Pattern = pattern, Capacity = capacity, CreatedAt = createdAt };
            var regex = new Regex(pattern);
            lock (_gate)
            {
                _watches[name] = new WatchEntry { Definition = def, Regex = regex, Records = new Queue<LogWatchRecord>() };
            }
            PersistManifest();
            PersistRecords(name);
        }

        internal static void Unregister(string name)
        {
            lock (_gate) _watches.Remove(name);
            PersistManifest();
            try { File.Delete(RecordsPath(name)); } catch (Exception ex) { Debug.LogWarning($"[UnityBridge] Failed to delete log watch file for \"{name}\": {ex.Message}"); }
        }

        // Called from LogBuffer.OnLogMessage for every incoming Console
        // message, on whatever thread logged it. Returns true if at least
        // one watch matched — LogBuffer uses that to skip adding the
        // message to its own shared ring buffer (LOCKED, "Raw-buffer
        // overlap": a matched message is diverted, never double-counted).
        // In-memory only; disk writes are debounced via FlushIfDue().
        internal static bool TryRoute(string message, string time)
        {
            bool matchedAny = false;
            lock (_gate)
            {
                foreach (var entry in _watches.Values)
                {
                    Match m;
                    try { m = entry.Regex.Match(message); }
                    catch { continue; } // defensive — a validated-at-registration regex shouldn't throw at match time
                    if (!m.Success) continue;

                    matchedAny = true;
                    var values = new Dictionary<string, object>();
                    foreach (var groupName in entry.Regex.GetGroupNames())
                    {
                        if (int.TryParse(groupName, out _)) continue; // numbered groups aren't named capture groups
                        var g = m.Groups[groupName];
                        if (g.Success) values[groupName] = g.Value;
                    }

                    entry.Records.Enqueue(new LogWatchRecord { Values = values, Time = time });
                    while (entry.Records.Count > entry.Definition.Capacity) entry.Records.Dequeue();
                    entry.Dirty = true;
                }
            }

            // Only arm the timer on the transition into "dirty," not on
            // every match — see LogBuffer.OnLogMessage's comment on the
            // same fix (starvation under continuous chatty logging turns a
            // debounce into "never flushes").
            if (matchedAny && _lastDirtyTime < 0) _lastDirtyTime = EditorApplication.timeSinceStartup;
            return matchedAny;
        }

        // Called every Tick() (main thread). Debounced like
        // BridgeAssetPostprocessor.FlushIfDue() — a burst of matches within
        // the same fraction of a second collapses into one file write per
        // dirty watch instead of one per message.
        internal static void FlushIfDue()
        {
            if (_lastDirtyTime < 0) return;
            if (EditorApplication.timeSinceStartup - _lastDirtyTime < DebounceSeconds) return;
            ForceFlush();
        }

        // Bypasses the debounce window — called from OnBeforeAssemblyReload
        // so a match that arrived less than DebounceSeconds before a
        // recompile still reaches disk before the static state resets
        // (LOCKED acceptance criterion 3: a plain recompile must not lose
        // watch data). Dirty flags are read and cleared entirely inside the
        // lock (unlike a separate outside-the-lock "pending" flag) so a
        // match arriving concurrently with a flush is never silently
        // dropped — it either lands in this pass's snapshot or sets Dirty
        // again after, both of which get persisted.
        internal static void ForceFlush()
        {
            List<string> dirtyNames = new List<string>();
            lock (_gate)
            {
                foreach (var entry in _watches.Values)
                {
                    if (!entry.Dirty) continue;
                    entry.Dirty = false;
                    dirtyNames.Add(entry.Definition.Name);
                }
            }

            if (dirtyNames.Count == 0) return;
            _lastDirtyTime = -1;
            foreach (var name in dirtyNames) PersistRecords(name);
        }

        static void PersistManifest()
        {
            List<LogWatchDefinition> defs;
            lock (_gate) defs = _watches.Values.Select(w => w.Definition).ToList();

            string dir = WatchesDir;
            Directory.CreateDirectory(dir);
            var dict = new Dictionary<string, object> { { "watches", defs.Select(d => (object)d.ToDict()).ToList() } };
            string tempPath = ManifestPath + ".tmp";
            File.WriteAllText(tempPath, MiniJson.Write(dict));
            if (File.Exists(ManifestPath)) File.Replace(tempPath, ManifestPath, null);
            else File.Move(tempPath, ManifestPath);
        }

        static void PersistRecords(string name)
        {
            List<LogWatchRecord> records;
            lock (_gate)
            {
                records = _watches.TryGetValue(name, out var entry)
                    ? new List<LogWatchRecord>(entry.Records)
                    : new List<LogWatchRecord>();
            }
            JsonLinesIO.WriteLines(RecordsPath(name), records, r => r.ToDict());
        }
    }
}
