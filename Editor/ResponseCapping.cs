using System.Collections.Generic;
using System.Text;

namespace UnityBridge.Editor
{
    // LOCKED (task brief, "Endpoints" section): every response is capped at
    // 16KB, and truncation is structural — drop whole list items until the
    // body fits, never truncate mid-value. Every truncated response carries
    // truncated/total/hint. Shared here so Phase 2's two endpoints (and
    // Phase 3's) apply the same contract instead of each re-deriving it.
    internal static class ResponseCapping
    {
        const int MaxBytes = 16384;

        // `total` is the true match count before any capping (limit or byte
        // size) — the caller passes it in since IndexStore already knows it
        // before applying `limit`.
        internal static void ApplyListCap(Dictionary<string, object> body, string listKey, int total, string hint)
        {
            if (!(body.TryGetValue(listKey, out var raw)) || !(raw is List<object> list))
            {
                body["truncated"] = false;
                body["total"] = total;
                return;
            }

            while (list.Count > 0 && Encoding.UTF8.GetByteCount(MiniJson.Write(body)) > MaxBytes)
            {
                list.RemoveAt(list.Count - 1);
            }

            bool truncated = list.Count < total;
            body["truncated"] = truncated;
            body["total"] = total;
            if (truncated) body["hint"] = hint;
        }
    }
}
