using System;
using System.Collections.Generic;
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
    internal static class LogBuffer
    {
        const int Capacity = 1000;

        static readonly object _gate = new object();
        static readonly Queue<LogEntryRecord> _entries = new Queue<LogEntryRecord>(Capacity);

        // Matches Application.LogCallback's signature exactly so it can be
        // registered directly against logMessageReceivedThreaded, which may
        // fire from any thread — the lock is what makes this safe.
        internal static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            var record = new LogEntryRecord
            {
                Severity = MapSeverity(type),
                Message = condition,
                StackFrame = FirstLine(stackTrace),
                Time = DateTime.UtcNow.ToString("o")
            };

            lock (_gate)
            {
                _entries.Enqueue(record);
                while (_entries.Count > Capacity) _entries.Dequeue();
            }
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
