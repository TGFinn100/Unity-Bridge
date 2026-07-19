using System;
using System.Collections.Generic;

namespace UnityBridge.Editor
{
    // Single-pending-action guard + deferred main-thread execution for
    // /act/* endpoints (LOCKED, v1.5 task brief). At most one
    // scheduled-but-not-yet-applied action may be in flight at a time,
    // shared across /act/playmode/enter, /act/playmode/exit, and
    // /act/refresh.
    //
    // Why: an /act handler responds 202 BEFORE the actual state change
    // runs (it's scheduled for the next Tick(), not applied inline), so
    // readyState doesn't change until that scheduled application runs.
    // Without this guard, a burst of requests arriving before the next
    // Tick() would each observe the same pre-toggle state, each pass their
    // own idempotence check, and each get accepted — e.g. two "enter"
    // requests both scheduling a toggle. Schedule() runs the whole
    // pending-check + idempotence-check + accept sequence atomically under
    // one lock so that can't happen.
    internal static class ActionScheduler
    {
        static readonly object _gate = new object();
        static Action _pendingAction;

        // Runs entirely inside the lock. buildAct(requestBody) performs its
        // own idempotence check against cached state and returns either a
        // 202 success (body + the actual main-thread action) or a conflict
        // response (409 already_in_state, no action). If another action is
        // already pending, returns 409 action_pending WITHOUT calling
        // buildAct at all — LOCKED ordering (pending-check strictly before
        // idempotence-check): computing idempotence against a value that's
        // about to change by the pending action is pointless.
        //
        // requestBody is threaded through (v1.6, for /act/logs/watch's
        // {name,pattern,capacity?}) — playmode/refresh's buildAct simply
        // ignores it, same as any handler ignoring an unused param.
        internal static (int status, Dictionary<string, object> body) Schedule(
            Func<Dictionary<string, object>, ActBuildResult> buildAct, Dictionary<string, object> requestBody)
        {
            lock (_gate)
            {
                if (_pendingAction != null)
                {
                    return (409, new Dictionary<string, object> { { "tier", "act" }, { "error", "action_pending" } });
                }

                var result = buildAct(requestBody);
                if (result.Status == 202) _pendingAction = result.MainThreadAction;
                return (result.Status, result.Body);
            }
        }

        // Called every Tick() (main thread), same dispatcher as everything
        // else. Runs at most one action per tick — these are rare,
        // user/agent-triggered events, not a throughput path.
        internal static void RunPendingIfAny()
        {
            Action action;
            lock (_gate)
            {
                action = _pendingAction;
                _pendingAction = null;
            }
            action?.Invoke();
        }
    }
}
