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
        public Func<BridgeRequestContext, Dictionary<string, object>> Handler;
    }

    internal readonly struct BridgeRequestContext
    {
        public readonly string Topic;
        public BridgeRequestContext(string topic) { Topic = topic; }
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

            if (method == "GET" && path.StartsWith("/help/"))
            {
                topic = path.Substring("/help/".Length).Trim('/');
                return _entries.Find(e => e.IsTopicRoute);
            }

            return null;
        }

        internal static EndpointInfo FindByTopic(string topicKey)
        {
            return _entries.Find(e => !e.IsTopicRoute && e.TopicKey == topicKey);
        }

        internal static IEnumerable<string> AllTopics()
        {
            foreach (var e in _entries)
                if (!e.IsTopicRoute) yield return e.TopicKey;
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            if (path.Length > 1 && path.EndsWith("/")) path = path.TrimEnd('/');
            return path;
        }
    }
}
