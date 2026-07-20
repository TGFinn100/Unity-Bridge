using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityBridge.Editor
{
    // Thread-safe snapshot of Unity Editor state. Unity API is only safe to
    // call from the main thread, but the HTTP timeout path runs on a
    // background thread and still needs readyState/elapsed info for its 504
    // body (LOCKED). RefreshFromMainThread() is called every dispatcher tick
    // so the cache stays fresh for as long as the main thread is ticking at
    // all — which covers every state except the brief "reloading" window,
    // where the listener socket is already closed anyway.
    internal static class BridgeState
    {
        internal static volatile string CachedReadyState = "compiling";
        internal static volatile string CachedUnityVersion = "";
        internal static volatile string CachedProjectName = "";

        // v1.5: whether a play-mode enter/exit toggle right now would
        // trigger a domain reload — derived from Configurable Enter Play
        // Mode settings, refreshed here (main thread) so /act/playmode/*
        // handlers can read it from the request thread without touching
        // EditorSettings directly. Best-effort per the v1.5 brief.
        internal static volatile bool CachedWillReloadOnPlaymodeToggle = true;

        // v1.7: structured compile error/warning signal, sourced from
        // CompilationPipeline.assemblyCompilationFinished (Unity's own
        // compiler output, not text scraped from Editor.log/Debug.Log —
        // see the task brief's "Mechanism" section for why the latter is
        // unreliable for build-failure-class errors like CS1654).
        internal const int CompileMessageCap = 20;
        // Declared before CachedCompileMessages below deliberately — C#
        // initializes static fields in textual declaration order, so a
        // forward reference here would silently capture null (a real bug
        // caught live: CachedCompileMessages held null until the first
        // AccumulateCompileMessages call, and /ping's Count on the null
        // snapshot threw a NullReferenceException before any compile had
        // even happened).
        static readonly IReadOnlyList<string> EmptyCompileMessages = new List<string>();
        internal static volatile int CachedCompileErrorCount = 0;
        internal static volatile int CachedCompileWarningCount = 0;
        internal static volatile IReadOnlyList<string> CachedCompileMessages = EmptyCompileMessages;

        // Main-thread-only accumulators (compilationStarted/
        // assemblyCompilationFinished both fire on the main thread, same as
        // the existing MarkCompiling() call site) — never read directly off
        // this thread; CachedCompileMessages is republished as a fresh list
        // on every update so a concurrent reader always sees a consistent
        // snapshot via the volatile reference.
        static readonly List<string> _accumulatedErrors = new List<string>();
        static readonly List<string> _accumulatedWarnings = new List<string>();

        internal static void RefreshFromMainThread()
        {
            string state;
            if (EditorApplication.isCompiling) state = "compiling";
            else if (!IndexStore.IsReady) state = "indexing";
            else if (EditorApplication.isPlaying) state = "playmode";
            else state = "ready";

            SetReadyState(state);
            CachedUnityVersion = Application.unityVersion;
            CachedProjectName = Application.productName;
            CachedWillReloadOnPlaymodeToggle = !(EditorSettings.enterPlayModeOptionsEnabled &&
                EditorSettings.enterPlayModeOptions.HasFlag(EnterPlayModeOptions.DisableDomainReload));
        }

        internal static void MarkReloading()
        {
            SetReadyState("reloading");
        }

        // Reset at CompilationPipeline.compilationStarted — the same
        // moment MarkCompiling() already fires (LOCKED) — so a fresh
        // compile pass always starts from a clean slate before any
        // assemblyCompilationFinished event for it has run.
        internal static void ResetCompileState()
        {
            _accumulatedErrors.Clear();
            _accumulatedWarnings.Clear();
            CachedCompileErrorCount = 0;
            CachedCompileWarningCount = 0;
            CachedCompileMessages = EmptyCompileMessages;
        }

        // Accumulates across every assemblyCompilationFinished call between
        // one compilationStarted and the next ResetCompileState() — a
        // single compile pass can touch multiple assemblies, each firing
        // this event separately (LOCKED). CachedCompileErrorCount/
        // CachedCompileWarningCount always reflect the true full counts;
        // only the displayed CachedCompileMessages list is capped, errors
        // ordered before warnings so an error can never be pushed out by
        // warning noise.
        internal static void AccumulateCompileMessages(CompilerMessage[] messages)
        {
            foreach (var m in messages)
            {
                string formatted = $"{m.file}: {m.message}";
                if (m.type == CompilerMessageType.Error)
                {
                    _accumulatedErrors.Add(formatted);
                    CachedCompileErrorCount++;
                }
                else if (m.type == CompilerMessageType.Warning)
                {
                    _accumulatedWarnings.Add(formatted);
                    CachedCompileWarningCount++;
                }
            }

            var capped = new List<string>(CompileMessageCap);
            foreach (var e in _accumulatedErrors)
            {
                if (capped.Count >= CompileMessageCap) break;
                capped.Add(e);
            }
            foreach (var w in _accumulatedWarnings)
            {
                if (capped.Count >= CompileMessageCap) break;
                capped.Add(w);
            }
            CachedCompileMessages = capped;
        }

        // Shared by /ping and /act/playmode/enter's 409 body — both need
        // the same capped list as a MiniJson-writable List<object>
        // (MiniJson.Write only dispatches IEnumerable<object>, not
        // IEnumerable<string> — see response-envelope.md).
        internal static List<object> CompileMessagesAsObjectList()
        {
            var snapshot = CachedCompileMessages; // one volatile read — atomic, consistent snapshot
            var list = new List<object>(snapshot.Count);
            foreach (var s in snapshot) list.Add(s);
            return list;
        }

        // Event-driven, unlike RefreshFromMainThread's isCompiling check: that
        // check only runs inside Tick() (EditorApplication.update), which Unity
        // throttles while the Editor is unfocused and can miss entirely if
        // compilation finishes before the next tick. CompilationPipeline fires
        // this synchronously on the main thread the instant compilation starts,
        // so it can't be raced out the same way.
        internal static void MarkCompiling()
        {
            SetReadyState("compiling");
        }

        // Live responses are frame-stale by definition during play mode
        // (task brief, "Failure modes to handle explicitly" — play mode).
        // Every live-tier handler calls this after building its body so the
        // caller can tell which frame the answer was current as of.
        internal static void AddFrameIfPlaying(Dictionary<string, object> body)
        {
            if (EditorApplication.isPlaying) body["frame"] = Time.frameCount;
        }

        // Logged so transitions that are too fast to catch with a manual
        // browser refresh (e.g. a trivial one-file recompile) are still
        // visible afterward in the Console / Logs/Editor.log.
        static void SetReadyState(string state)
        {
            if (state == CachedReadyState) return;
            Debug.Log($"[UnityBridge] readyState: {CachedReadyState} -> {state}");
            CachedReadyState = state;
        }
    }
}
