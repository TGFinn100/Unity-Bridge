using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityBridge.Editor
{
    internal sealed class LogEntryRecord
    {
        public string Severity; // "error" | "warn" | "log"
        public string Message;
        public string StackFrame; // first line of the stack trace only, per task brief
        public string Time; // ISO UTC

        public Dictionary<string, object> ToDict() => new Dictionary<string, object>
        {
            { "severity", Severity },
            { "message", Message },
            { "stackFrame", StackFrame },
            { "time", Time }
        };

        public static LogEntryRecord FromDict(Dictionary<string, object> d) => new LogEntryRecord
        {
            Severity = (string)d["severity"],
            Message = (string)d["message"],
            StackFrame = (string)d["stackFrame"],
            Time = (string)d["time"]
        };
    }

    // Bounded ring buffer of Console entries, fed by Application's own log
    // event rather than reflecting into Unity's internal LogEntries API —
    // no version-specific internal surface to break across Unity upgrades,
    // at the cost of only capturing entries logged after the bridge started
    // (acceptable: the bridge is already running before any agent session
    // begins). Subscribed once from BridgeServer's static ctor; no explicit
    // unsubscribe needed since a domain reload discards the old delegate
    // along with the rest of the old assembly, same as the other
    // process-lifetime event hooks in BridgeServer.
    //
    // v1.6 LOCKED: gains disk persistence (log_buffer.jsonl) — previously
    // purely in-memory, resetting on every reload including plain
    // recompiles. Same wipe-on-Play-Mode-entry lifecycle as LogWatchStore;
    // see Load() and its comment for the mechanism.
    internal static class LogBuffer
    {
        const int Capacity = 1000;
        const double DebounceSeconds = 0.25; // matches LogWatchStore's debounce window

        static readonly object _gate = new object();
        static readonly Queue<LogEntryRecord> _entries = new Queue<LogEntryRecord>(Capacity);
        static bool _dirty; // guarded by _gate — authoritative; _lastDirtyTime below is only a debounce timer
        static double _lastDirtyTime = -1;

        static string BufferPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "log_buffer.jsonl");

        // Pure file I/O — safe to call from BridgeServer's static
        // constructor before any main-thread tick has run. Mirrors
        // LogWatchStore.Load()'s wipe check exactly — see
        // LogPersistenceLifecycle's comment for why this checks a marker
        // file rather than EditorApplication.isPlaying (the LOCKED brief's
        // original mechanism, disproved by live testing).
        //
        // enteringPlayMode is computed ONCE by the caller
        // (LogPersistenceLifecycle.ConsumeEnteringPlayModeMarker(), called
        // exactly once from BridgeServer) and passed in here rather than
        // consumed by this method directly — see that method's comment for
        // the double-consumption bug this avoids.
        internal static void Load(bool enteringPlayMode)
        {
            if (enteringPlayMode)
            {
                try { File.Delete(BufferPath); } catch (Exception ex) { Debug.LogWarning($"[UnityBridge] Failed to wipe log buffer on Play Mode entry: {ex.Message}"); }
                return;
            }

            try
            {
                var records = JsonLinesIO.ReadLines(BufferPath, LogEntryRecord.FromDict);
                lock (_gate)
                {
                    _entries.Clear();
                    foreach (var r in records) _entries.Enqueue(r);
                }
            }
            catch (Exception ex)
            {
                // Corrupt/partial jsonl — self-heal by starting empty rather
                // than crashing, same as IndexStore.Load()'s handling.
                Debug.LogWarning($"[UnityBridge] Log buffer load failed, starting empty: {ex.Message}");
            }
        }

        // Matches Application.LogCallback's signature exactly so it can be
        // registered directly against logMessageReceivedThreaded, which may
        // fire from any thread — the lock is what makes this safe.
        internal static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            string time = DateTime.UtcNow.ToString("o");

            // v1.6 LOCKED "Raw-buffer overlap": a message matching an active
            // watch is diverted to that watch's condensed store only — it
            // never also consumes a raw-buffer slot.
            if (LogWatchStore.TryRoute(condition, time)) return;

            var record = new LogEntryRecord
            {
                Severity = MapSeverity(type),
                Message = condition,
                StackFrame = FirstLine(stackTrace),
                Time = time
            };

            lock (_gate)
            {
                _entries.Enqueue(record);
                while (_entries.Count > Capacity) _entries.Dequeue();
                _dirty = true;
            }
            // Only arm the timer on the transition into "dirty" (not on
            // every message) — a real bug caught during v1.6 acceptance
            // testing 2026-07-19: setting this unconditionally on every
            // call turns the debounce into a starvation trap under
            // continuous chatty logging (a message arriving every ~10ms
            // keeps pushing the timer forward forever, so the "quiet long
            // enough to flush" condition never occurs and the on-disk file
            // never gets written outside of the guaranteed reload-boundary
            // ForceFlush). Setting it once per dirty streak instead makes
            // this a throttle (flush every ~250ms even under sustained
            // load) rather than a pure debounce.
            if (_lastDirtyTime < 0) _lastDirtyTime = EditorApplication.timeSinceStartup;
        }

        // Oldest-first (buffer's natural enqueue order) — callers that want
        // newest-first (e.g. /logs/tail) reverse after filtering/taking n.
        internal static List<LogEntryRecord> Snapshot()
        {
            lock (_gate)
            {
                return new List<LogEntryRecord>(_entries);
            }
        }

        // Called every Tick() (main thread). Debounced same as
        // LogWatchStore.FlushIfDue() — a burst of log messages within the
        // same fraction of a second collapses into one file write. The
        // debounce timer is only a soft gate on when to check; ForceFlush()
        // itself is the authoritative dirty-check, so this never skips a
        // genuinely pending write past the debounce window.
        internal static void FlushIfDue()
        {
            if (_lastDirtyTime >= 0 && EditorApplication.timeSinceStartup - _lastDirtyTime < DebounceSeconds) return;
            ForceFlush();
        }

        // Bypasses the debounce window — called from OnBeforeAssemblyReload
        // so a message logged less than DebounceSeconds before a recompile
        // still reaches disk before the static state resets (LOCKED
        // acceptance criterion 3). Dirty is checked and cleared entirely
        // inside the lock, atomically with the snapshot, so a message that
        // arrives concurrently with this call either lands in this
        // snapshot or leaves _dirty set for the very next call — never
        // silently dropped.
        internal static void ForceFlush()
        {
            List<LogEntryRecord> snapshot;
            lock (_gate)
            {
                if (!_dirty) return;
                _dirty = false;
                snapshot = new List<LogEntryRecord>(_entries);
            }
            _lastDirtyTime = -1;
            JsonLinesIO.WriteLines(BufferPath, snapshot, r => r.ToDict());
        }

        static string MapSeverity(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Assert:
                case LogType.Exception:
                    return "error";
                case LogType.Warning:
                    return "warn";
                default:
                    return "log";
            }
        }

        static string FirstLine(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace)) return "";
            int newline = stackTrace.IndexOf('\n');
            return newline >= 0 ? stackTrace.Substring(0, newline).TrimEnd('\r') : stackTrace;
        }
    }
}
