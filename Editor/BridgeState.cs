using System.Collections.Generic;
using UnityEditor;
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
