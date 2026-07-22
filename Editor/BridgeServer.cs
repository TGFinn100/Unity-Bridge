using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityBridge.Editor
{
    internal sealed class PendingRequest
    {
        public EndpointInfo Endpoint;
        public string Topic;
        public Dictionary<string, object> Body;
        public IReadOnlyDictionary<string, string> Query;
        public TaskCompletionSource<BridgeHandlerResult> Tcs;
        public DateTime EnqueuedAt;
    }

    // v2: the synchronous-mutation counterpart to PendingRequest — simpler,
    // since a mutation endpoint has no Topic/Query (all 8 take a POST body
    // only) and BuildMutation already returns a full BridgeHandlerResult
    // rather than needing InvokeHandler's Handler-plus-exception-mapping
    // wrapper (that mapping still applies, just around BuildMutation
    // instead — see BridgeServer.DrainMutationQueue).
    internal sealed class PendingMutation
    {
        public Func<Dictionary<string, object>, BridgeHandlerResult> BuildMutation;
        public Dictionary<string, object> Body;
        public TaskCompletionSource<BridgeHandlerResult> Tcs;
        public DateTime EnqueuedAt;
    }

    // Listener bound to 127.0.0.1 only (LOCKED — never bind 0.0.0.0), port
    // walk 17870-17879, bound port written atomically to
    // Library/UnityBridge/port. [InitializeOnLoad] re-runs this whole class's
    // static constructor after every domain reload, which is what
    // "restarts the listener after every reload" means in practice — all
    // static state below resets for free on every reload.
    [InitializeOnLoad]
    internal static class BridgeServer
    {
        const int PortRangeStart = 17870;
        const int PortRangeEnd = 17879;
        const int DefaultTimeoutMs = 5000;

        static HttpListener _listener;
        static Thread _acceptThread;
        static readonly System.Collections.Concurrent.ConcurrentQueue<PendingRequest> _queue =
            new System.Collections.Concurrent.ConcurrentQueue<PendingRequest>();
        // v2: separate queue for Tier=="act" && Synchronous endpoints — kept
        // apart from _queue (rather than reusing it) since mutation dispatch
        // additionally holds ActionScheduler's mutation slot for the whole
        // round trip and returns a BridgeHandlerResult straight from
        // BuildMutation, not via Handler+InvokeHandler's tier-specific
        // envelope assumptions (no "tier" auto-fill, no AddFrameIfPlaying).
        static readonly System.Collections.Concurrent.ConcurrentQueue<PendingMutation> _mutationQueue =
            new System.Collections.Concurrent.ConcurrentQueue<PendingMutation>();

        internal static int BoundPort { get; private set; } = -1;

        static BridgeServer()
        {
            IndexStore.Load(); // pure file I/O — safe before any Tick() has run
            ActionToken.EnsureExists(); // pure file I/O — same as above
            // v1.6: consumed exactly ONCE here, not inside LogWatchStore/
            // LogBuffer individually — each Load() call below reads the
            // marker file and deletes it, so calling this per-store would
            // let only the first one see it (real bug caught during
            // acceptance testing, see LogPersistenceLifecycle's comment).
            bool enteringPlayMode = LogPersistenceLifecycle.ConsumeEnteringPlayModeMarker();
            LogWatchStore.Load(enteringPlayMode); // pure file I/O — wipes on Play Mode entry
            LogBuffer.Load(enteringPlayMode); // pure file I/O — same wipe-on-Play-Mode-entry lifecycle
            RegisterEndpoints();
            StartListener();
            EditorApplication.update += Tick;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished; // v1.7
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            EditorApplication.playModeStateChanged += PlaymodeEndpoint.OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += LogPersistenceLifecycle.OnPlayModeStateChanged; // v1.6
            Application.logMessageReceivedThreaded += LogBuffer.OnLogMessage;
        }

        static void RegisterEndpoints()
        {
            PingEndpoint.Register();
            HelpEndpoint.Register();
            QueryEndpoint.Register();
            AssetEndpoint.Register();
            SceneSummaryEndpoint.Register();
            ObjectEndpoint.Register();
            LogsTailEndpoint.Register();
            PlaymodeEndpoint.Register();
            ActPlaymodeEndpoint.Register();
            ActRefreshEndpoint.Register();
            LogsWatchesEndpoint.Register();
            LogsWatchEndpoint.Register();
            ActLogsWatchEndpoint.Register();
            ActLogsUnwatchEndpoint.Register();
            GameObjectCreateEndpoint.Register();
            GameObjectDeleteEndpoint.Register();
            GameObjectDuplicateEndpoint.Register();
            GameObjectReparentEndpoint.Register();
            GameObjectRenameEndpoint.Register();
            ComponentAddEndpoint.Register();
            ComponentRemoveEndpoint.Register();
            ComponentSetFieldEndpoint.Register();
        }

        static void StartListener()
        {
            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    listener.Start();

                    _listener = listener;
                    BoundPort = port;
                    WritePortFileAtomic(port);

                    _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "UnityBridge-Accept" };
                    _acceptThread.Start();
                    Debug.Log($"[UnityBridge] Listening on http://127.0.0.1:{port}/");
                    return;
                }
                catch (HttpListenerException)
                {
                    // Port taken — walk to the next one.
                }
            }

            Debug.LogError($"[UnityBridge] Could not bind any port in range {PortRangeStart}-{PortRangeEnd}.");
        }

        static void WritePortFileAtomic(int port)
        {
            string dir = ProjectPaths.LibraryUnityBridgeDir;
            Directory.CreateDirectory(dir);
            string finalPath = Path.Combine(dir, "port");
            string tempPath = finalPath + ".tmp";

            File.WriteAllText(tempPath, port.ToString());
            if (File.Exists(finalPath))
                File.Replace(tempPath, finalPath, null);
            else
                File.Move(tempPath, finalPath);
        }

        static void AcceptLoop()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = _listener.GetContext(); // blocks until a request arrives or the listener is closed
                }
                catch
                {
                    return; // listener stopped/disposed — exit the accept loop cleanly
                }

                var capturedCtx = ctx;
                ThreadPool.QueueUserWorkItem(_ => HandleRequest(capturedCtx));
            }
        }

        static void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string hostHeader = ctx.Request.Headers["Host"];
                if (!IsLocalHost(hostHeader))
                {
                    WriteJson(ctx, 403, new Dictionary<string, object> { { "error", "Host header must be localhost or 127.0.0.1" } });
                    return;
                }

                string method = ctx.Request.HttpMethod;
                string path = ctx.Request.Url.AbsolutePath;
                IReadOnlyDictionary<string, string> query = ParseQueryString(ctx.Request.Url.Query);

                EndpointInfo endpoint = EndpointRegistry.Resolve(method, path, out string topic);
                if (endpoint == null)
                {
                    WriteJson(ctx, 404, new Dictionary<string, object> { { "error", "unknown endpoint — see /help" } });
                    return;
                }

                Dictionary<string, object> parsedBody = null;
                if (method == "POST" && ctx.Request.HasEntityBody)
                {
                    string rawBody;
                    using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
                        rawBody = reader.ReadToEnd();

                    if (!string.IsNullOrWhiteSpace(rawBody))
                    {
                        try
                        {
                            parsedBody = MiniJson.Parse(rawBody) as Dictionary<string, object>;
                        }
                        catch (Exception)
                        {
                            WriteJson(ctx, 400, new Dictionary<string, object> { { "error", "malformed JSON body" } });
                            return;
                        }
                    }
                }

                if (endpoint.Tier == "meta")
                {
                    // Meta-tier endpoints (/ping, /help) only read pre-cached,
                    // thread-safe state and never call into the Unity API, so
                    // they're answered directly on this background thread
                    // instead of the main-thread queue below. This matters
                    // because Unity can run a "forced synchronous recompile"
                    // that blocks EditorApplication.update (and therefore
                    // Tick()) for the whole compile+reload — routing meta
                    // requests through that queue would make readyState
                    // "compiling" unobservable exactly when it's needed.
                    var directResult = InvokeHandler(endpoint, topic, parsedBody, query);
                    WriteJson(ctx, directResult.Status, directResult.Body);
                    return;
                }

                if (endpoint.Tier == "indexed")
                {
                    // Indexed-tier reads answer directly from the in-memory
                    // index on this thread too (LOCKED — task brief:
                    // "no main thread involved, slow means broken, fail
                    // fast"), never via the main-thread queue below. The
                    // index store itself is what's thread-safe here (a lock
                    // around its lists), not this dispatch.
                    if (!IndexStore.IsReady)
                    {
                        WriteJson(ctx, 503, new Dictionary<string, object>
                        {
                            { "tier", "indexed" },
                            { "error", "indexing" },
                            { "readyState", BridgeState.CachedReadyState }
                        });
                        return;
                    }
                    var directResult = InvokeHandler(endpoint, topic, parsedBody, query);
                    WriteJson(ctx, directResult.Status, directResult.Body);
                    return;
                }

                if (endpoint.Tier == "act")
                {
                    // v1.5: token check first (LOCKED ordering) — an
                    // invalid/missing token always returns 401
                    // unconditionally, before anything else (readyState,
                    // pending-guard, idempotence) is even evaluated, so an
                    // unauthenticated caller can't learn current state via
                    // a 409/503 that would otherwise fire first.
                    string presentedToken = ctx.Request.Headers["X-Bridge-Token"];
                    if (!ActionToken.IsValid(presentedToken))
                    {
                        WriteJson(ctx, 401, new Dictionary<string, object> { { "error", "unauthorized" }, { "tier", "act" } });
                        return;
                    }

                    string actReadyState = BridgeState.CachedReadyState;
                    if (actReadyState != "ready" && actReadyState != "playmode")
                    {
                        // Never queue an action across a reload (LOCKED,
                        // v1.5 brief "Failure modes") — same 503 shape as
                        // the indexed tier's not-ready case.
                        WriteJson(ctx, 503, new Dictionary<string, object>
                        {
                            { "tier", "act" }, { "error", "not_ready" }, { "readyState", actReadyState }
                        });
                        return;
                    }

                    // v2: GameObject-lifecycle/component-mutation endpoints
                    // share every check above (token, readiness) but branch
                    // here into a genuinely different dispatch — synchronous
                    // main-thread execution that blocks this request thread
                    // and returns the real 200 result in the same round
                    // trip, instead of BuildAct's 202-then-deferred-Tick
                    // pattern. See routing-and-tiers.md's "Synchronous
                    // mutation dispatch (v2)" section.
                    if (endpoint.Synchronous)
                    {
                        if (!ActionScheduler.TryEnterMutation())
                        {
                            WriteJson(ctx, 409, new Dictionary<string, object> { { "tier", "act" }, { "error", "action_pending" } });
                            return;
                        }

                        try
                        {
                            var pendingMutation = new PendingMutation
                            {
                                BuildMutation = endpoint.BuildMutation,
                                Body = parsedBody,
                                Tcs = new TaskCompletionSource<BridgeHandlerResult>(),
                                EnqueuedAt = DateTime.UtcNow
                            };
                            _mutationQueue.Enqueue(pendingMutation);

                            bool mutationCompleted = pendingMutation.Tcs.Task.Wait(endpoint.TimeoutMs);
                            if (mutationCompleted)
                            {
                                var result = pendingMutation.Tcs.Task.Result;
                                WriteJson(ctx, result.Status, result.Body);
                            }
                            else
                            {
                                double elapsedMs = (DateTime.UtcNow - pendingMutation.EnqueuedAt).TotalMilliseconds;
                                WriteJson(ctx, 504, new Dictionary<string, object>
                                {
                                    { "tier", "act" }, { "error", "timeout" },
                                    { "readyState", BridgeState.CachedReadyState }, { "elapsedMs", Math.Round(elapsedMs, 1) }
                                });
                            }
                        }
                        finally
                        {
                            // Released only after the mutation has actually
                            // run (or timed out) — the reservation spans the
                            // whole synchronous round trip, per the LOCKED
                            // "acquire the lock for the duration" execution
                            // model, not just the enqueue step.
                            ActionScheduler.ExitMutation();
                        }
                        return;
                    }

                    var (actStatus, actBody) = ActionScheduler.Schedule(endpoint.BuildAct, parsedBody);
                    WriteJson(ctx, actStatus, actBody);
                    return;
                }

                var pending = new PendingRequest
                {
                    Endpoint = endpoint,
                    Topic = topic,
                    Body = parsedBody,
                    Query = query,
                    Tcs = new TaskCompletionSource<BridgeHandlerResult>(),
                    EnqueuedAt = DateTime.UtcNow
                };
                _queue.Enqueue(pending);

                bool completed = pending.Tcs.Task.Wait(endpoint.TimeoutMs);
                if (completed)
                {
                    var result = pending.Tcs.Task.Result;
                    WriteJson(ctx, result.Status, result.Body);
                }
                else
                {
                    double elapsedMs = (DateTime.UtcNow - pending.EnqueuedAt).TotalMilliseconds;
                    var body = new Dictionary<string, object>
                    {
                        { "tier", endpoint.Tier },
                        { "error", "timeout" },
                        { "readyState", BridgeState.CachedReadyState },
                        { "elapsedMs", Math.Round(elapsedMs, 1) }
                    };
                    WriteJson(ctx, 504, body);
                }
            }
            catch
            {
                // Connection likely gone (e.g. torn down mid-reload). Nothing to write to.
            }
        }

        // Hand-rolled instead of System.Web.HttpUtility — not reliably
        // available under Unity's Editor assembly API compatibility level,
        // and this codebase already avoids external parsing deps (MiniJson
        // exists for the same reason).
        static IReadOnlyDictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return result;
            if (query[0] == '?') query = query.Substring(1);

            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                string value = eq >= 0 ? pair.Substring(eq + 1) : "";
                key = Uri.UnescapeDataString(key);
                value = Uri.UnescapeDataString(value);
                if (key.Length > 0) result[key] = value;
            }

            return result;
        }

        static bool IsLocalHost(string hostHeader)
        {
            if (string.IsNullOrEmpty(hostHeader)) return false;
            string hostOnly = hostHeader.Split(':')[0];
            return hostOnly == "127.0.0.1" || hostOnly == "localhost";
        }

        static void WriteJson(HttpListenerContext ctx, int statusCode, Dictionary<string, object> body)
        {
            try
            {
                string json = MiniJson.Write(body);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                ctx.Response.StatusCode = statusCode;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // Connection likely gone — nothing more we can do.
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        }

        static void Tick()
        {
            ActionScheduler.RunPendingIfAny(); // v1.5: apply any accepted /act action first
            DrainMutationQueue(); // v2: synchronous GameObject/component mutations — before the index scan, same priority as act actions above
            IndexStore.RunFullScanIfNeeded();
            BridgeAssetPostprocessor.FlushIfDue();
            LogBuffer.FlushIfDue(); // v1.6: debounced disk persistence
            LogWatchStore.FlushIfDue(); // v1.6: debounced disk persistence
            BridgeState.RefreshFromMainThread();

            while (_queue.TryDequeue(out var pending))
            {
                pending.Tcs.TrySetResult(InvokeHandler(pending.Endpoint, pending.Topic, pending.Body, pending.Query));
            }
        }

        // v2: runs each queued mutation's BuildMutation directly on the main
        // thread (safe to touch the Unity API here, same as the rest of
        // Tick()). Exception mapping mirrors InvokeHandler: MutationRejection
        // carries its own full (status, body) — checked first since it's the
        // more specific case — BridgeHttpException maps to the standard
        // {"error": message} shape (used for the id-resolution chain, reused
        // verbatim from ObjectEndpoint), anything else is an unexpected 500.
        static void DrainMutationQueue()
        {
            while (_mutationQueue.TryDequeue(out var pending))
            {
                // v2 bug fix (found live during acceptance testing): Unity
                // only starts a new Undo *group* boundary when something
                // explicitly asks for one — in normal Editor use that
                // happens automatically between distinct UI interactions,
                // but nothing does it for scripted/HTTP-triggered mutations.
                // Without this, two mutations arriving close together (e.g.
                // create then rename, both driven by one agent turn) landed
                // in the SAME undo group, so a single Ctrl+Z silently
                // reverted both at once — confirmed live: after create+
                // rename, one undo removed the created object entirely
                // rather than just reverting the rename. Called once per
                // dequeued mutation, centrally here rather than in each of
                // the 8 endpoint files, so a future 9th mutation endpoint
                // can't forget it.
                Undo.IncrementCurrentGroup();

                BridgeHandlerResult result;
                try
                {
                    result = pending.BuildMutation(pending.Body);
                }
                catch (MutationRejection reject)
                {
                    result = new BridgeHandlerResult { Status = reject.Status, Body = reject.Body };
                }
                catch (BridgeHttpException httpEx)
                {
                    result = new BridgeHandlerResult
                    {
                        Status = httpEx.StatusCode,
                        Body = new Dictionary<string, object> { { "error", httpEx.Message } }
                    };
                }
                catch (Exception ex)
                {
                    result = new BridgeHandlerResult
                    {
                        Status = 500,
                        Body = new Dictionary<string, object> { { "error", ex.Message } }
                    };
                }
                pending.Tcs.TrySetResult(result);
            }
        }

        static BridgeHandlerResult InvokeHandler(EndpointInfo endpoint, string topic, Dictionary<string, object> requestBody, IReadOnlyDictionary<string, string> query)
        {
            try
            {
                var body = endpoint.Handler(new BridgeRequestContext(topic, requestBody, query));
                return new BridgeHandlerResult { Status = 200, Body = body };
            }
            catch (BridgeHttpException httpEx)
            {
                return new BridgeHandlerResult
                {
                    Status = httpEx.StatusCode,
                    Body = new Dictionary<string, object> { { "error", httpEx.Message } }
                };
            }
            catch (Exception ex)
            {
                return new BridgeHandlerResult
                {
                    Status = 500,
                    Body = new Dictionary<string, object> { { "error", ex.Message } }
                };
            }
        }

        static void OnCompilationStarted(object obj)
        {
            BridgeState.MarkCompiling();
            BridgeState.ResetCompileState(); // v1.7: same moment, LOCKED
        }

        // v1.7: fires per-assembly with that assembly's own CompilerMessage[]
        // — a single compile pass can call this multiple times, hence
        // BridgeState accumulating rather than replacing.
        static void OnAssemblyCompilationFinished(string assemblyPath, UnityEditor.Compilation.CompilerMessage[] messages)
        {
            BridgeState.AccumulateCompileMessages(messages);
        }

        static void OnBeforeAssemblyReload()
        {
            // v1.6: force any debounced-but-not-yet-written log data to disk
            // before the domain reload discards static state — otherwise a
            // message logged less than the debounce window before a
            // recompile could be lost even though the recompile itself
            // isn't supposed to wipe persisted log data (LOCKED acceptance
            // criterion 3).
            LogBuffer.ForceFlush();
            LogWatchStore.ForceFlush();
            BridgeState.MarkReloading();
            StopListener();
        }

        static void OnQuitting()
        {
            StopListener();
        }

        static void StopListener()
        {
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
        }

        // gate1-torture-marker: 10 — trivial no-op comment toggled by gate1-torture.sh each cycle to force a recompile
    }
}
