using System;
using System.Collections.Generic;

namespace UnityBridge.Editor
{
    internal sealed class EndpointInfo
    {
        public string Method;
        public string Path; // display path; "/help/{topic}" is display-only for the catch-all route
        public string TopicKey;
        public string Tier; // "meta" | "indexed" | "live"
        public string Summary; // <=8 words, shown in the /help index
        public string[] Params;
        public string ExampleRequest;
        public string ExampleResponseAbbrev;
        public int TimeoutMs;
        public bool IsTopicRoute;
        public string ParamPrefix; // e.g. "/help/" or "/asset/" — required when IsTopicRoute
        public Func<BridgeRequestContext, Dictionary<string, object>> Handler;
    }

    internal readonly struct BridgeRequestContext
    {
        public readonly string Topic; // captured path-param segment, e.g. the topic in /help/{topic} or the guid in /asset/{guid}
        public readonly Dictionary<string, object> Body; // parsed POST body, null for GET or an empty/missing body
        public readonly IReadOnlyDictionary<string, string> Query; // parsed GET query string, e.g. ?depth=1&components=values; never null (empty dict if none)
        public BridgeRequestContext(string topic, Dictionary<string, object> body = null, IReadOnlyDictionary<string, string> query = null)
        {
            Topic = topic;
            Body = body;
            Query = query ?? EmptyQuery;
        }

        static readonly Dictionary<string, string> EmptyQuery = new Dictionary<string, string>();
    }

    internal sealed class BridgeHandlerResult
    {
        public int Status;
        public Dictionary<string, object> Body;
    }

    // Thrown by a handler to produce a specific HTTP status + JSON error body
    // instead of the default 500.
    internal sealed class BridgeHttpException : Exception
    {
        public readonly int StatusCode;
        public BridgeHttpException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal static class EndpointRegistry
    {
        static readonly List<EndpointInfo> _entries = new List<EndpointInfo>();

        internal static IReadOnlyList<EndpointInfo> All => _entries;

        internal static void Add(EndpointInfo info) => _entries.Add(info);

        internal static EndpointInfo Resolve(string method, string path, out string topic)
        {
            topic = null;
            path = NormalizePath(path);

            foreach (var e in _entries)
            {
                if (e.IsTopicRoute) continue;
                if (e.Method == method && e.Path == path) return e;
            }

            // Param routes (/help/{topic}, /asset/{guid}, ...): match by
            // method + path prefix, capture the remainder as the param.
            foreach (var e in _entries)
            {
                if (!e.IsTopicRoute || e.Method != method) continue;
                if (string.IsNullOrEmpty(e.ParamPrefix) || !path.StartsWith(e.ParamPrefix)) continue;
                topic = path.Substring(e.ParamPrefix.Length).Trim('/');
                return e;
            }

            return null;
        }

        // IsTopicRoute is purely a routing concern (does this endpoint's own
        // path need prefix-matching against a captured param) and doesn't
        // exclude it from being documented — /help/{topic} and /asset/{guid}
        // are themselves valid topics (e.g. GET /help/asset), same as any
        // exact-match endpoint.
        internal static EndpointInfo FindByTopic(string topicKey)
        {
            return _entries.Find(e => e.TopicKey == topicKey);
        }

        internal static IEnumerable<string> AllTopics()
        {
            foreach (var e in _entries) yield return e.TopicKey;
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            if (path.Length > 1 && path.EndsWith("/")) path = path.TrimEnd('/');
            return path;
        }
    }
}
