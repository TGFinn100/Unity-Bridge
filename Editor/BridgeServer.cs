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

        internal static int BoundPort { get; private set; } = -1;

        static BridgeServer()
        {
            RegisterEndpoints();
            StartListener();
            EditorApplication.update += Tick;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
        }

        static void RegisterEndpoints()
        {
            PingEndpoint.Register();
            HelpEndpoint.Register();
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
            string dir = Path.Combine(GetProjectRoot(), "Library", "UnityBridge");
            Directory.CreateDirectory(dir);
            string finalPath = Path.Combine(dir, "port");
            string tempPath = finalPath + ".tmp";

            File.WriteAllText(tempPath, port.ToString());
            if (File.Exists(finalPath))
                File.Replace(tempPath, finalPath, null);
            else
                File.Move(tempPath, finalPath);
        }

        static string GetProjectRoot() => Path.GetDirectoryName(Application.dataPath);

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

                EndpointInfo endpoint = EndpointRegistry.Resolve(method, path, out string topic);
                if (endpoint == null)
                {
                    WriteJson(ctx, 404, new Dictionary<string, object> { { "error", "unknown endpoint — see /help" } });
                    return;
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
                    var directResult = InvokeHandler(endpoint, topic);
                    WriteJson(ctx, directResult.Status, directResult.Body);
                    return;
                }

                var pending = new PendingRequest
                {
                    Endpoint = endpoint,
                    Topic = topic,
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
            BridgeState.RefreshFromMainThread();

            while (_queue.TryDequeue(out var pending))
            {
                pending.Tcs.TrySetResult(InvokeHandler(pending.Endpoint, pending.Topic));
            }
        }

        static BridgeHandlerResult InvokeHandler(EndpointInfo endpoint, string topic)
        {
            try
            {
                var body = endpoint.Handler(new BridgeRequestContext(topic));
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
        }

        static void OnBeforeAssemblyReload()
        {
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
