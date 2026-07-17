using System.Collections.Generic;
using System.Linq;

namespace UnityBridge.Editor
{
    internal static class HelpEndpoint
    {
        internal static void Register()
        {
            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/help",
                TopicKey = "help",
                Tier = "meta",
                Summary = "Compact index of every endpoint",
                Params = System.Array.Empty<string>(),
                ExampleRequest = "GET /help",
                ExampleResponseAbbrev = "{\"tier\":\"meta\",\"endpoints\":[{\"method\":\"GET\",\"path\":\"/ping\",\"summary\":\"...\"}]}",
                TimeoutMs = 5000,
                Handler = HandleIndex
            });

            EndpointRegistry.Add(new EndpointInfo
            {
                Method = "GET",
                Path = "/help/{topic}",
                TopicKey = "help-topic",
                Tier = "meta",
                Summary = "Full docs for one endpoint",
                Params = new[] { "topic (string, required in URL path): endpoint topic key, e.g. \"ping\"" },
                ExampleRequest = "GET /help/ping",
                ExampleResponseAbbrev = "{\"tier\":\"meta\",\"method\":\"GET\",\"path\":\"/ping\",\"summary\":\"...\",\"params\":[],\"exampleRequest\":\"...\",\"exampleResponse\":\"...\"}",
                TimeoutMs = 5000,
                IsTopicRoute = true,
                ParamPrefix = "/help/",
                Handler = HandleTopic
            });
        }

        static Dictionary<string, object> HandleIndex(BridgeRequestContext ctx)
        {
            var list = new List<object>();
            foreach (var e in EndpointRegistry.All)
            {
                list.Add(new Dictionary<string, object>
                {
                    { "method", e.Method },
                    { "path", e.Path },
                    { "summary", e.Summary }
                });
            }

            return new Dictionary<string, object> { { "tier", "meta" }, { "endpoints", list } };
        }

        static Dictionary<string, object> HandleTopic(BridgeRequestContext ctx)
        {
            if (string.IsNullOrEmpty(ctx.Topic))
            {
                throw new BridgeHttpException(400, "missing topic — see /help for valid topics");
            }

            var entry = EndpointRegistry.FindByTopic(ctx.Topic);
            if (entry == null)
            {
                string valid = string.Join(", ", EndpointRegistry.AllTopics());
                throw new BridgeHttpException(404, $"unknown topic \"{ctx.Topic}\" — valid topics: {valid}");
            }

            return new Dictionary<string, object>
            {
                { "tier", "meta" },
                { "method", entry.Method },
                { "path", entry.Path },
                { "endpointTier", entry.Tier },
                { "summary", entry.Summary },
                { "params", entry.Params.Cast<object>().ToList() },
                { "exampleRequest", entry.ExampleRequest },
                { "exampleResponse", entry.ExampleResponseAbbrev }
            };
        }
    }
}
