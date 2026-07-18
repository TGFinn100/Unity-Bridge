using System;
using System.IO;
using System.Security.Cryptography;

namespace UnityBridge.Editor
{
    // Per-project token for /act/* auth (LOCKED, v1.5 task brief "Token
    // auth"). Generated once on first listener start, written atomically
    // next to the port file, stable across reloads — never regenerated
    // while Library/UnityBridge/token exists. Plain-text on disk is
    // acceptable: the threat model is "another localhost process/webpage
    // guessing the port and firing a mutation," not secret management for
    // a real credential — same reasoning as v1's "no auth needed while
    // read-only" note.
    internal static class ActionToken
    {
        static string TokenPath => Path.Combine(ProjectPaths.LibraryUnityBridgeDir, "token");

        static volatile string _cachedToken;

        // Pure file I/O — safe to call from BridgeServer's static
        // constructor before any main-thread tick has run, same as
        // IndexStore.Load().
        internal static void EnsureExists()
        {
            if (File.Exists(TokenPath))
            {
                _cachedToken = File.ReadAllText(TokenPath).Trim();
                return;
            }

            byte[] bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string hex = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

            string dir = ProjectPaths.LibraryUnityBridgeDir;
            Directory.CreateDirectory(dir);
            string tempPath = TokenPath + ".tmp";
            File.WriteAllText(tempPath, hex);
            if (File.Exists(TokenPath)) File.Replace(tempPath, TokenPath, null);
            else File.Move(tempPath, TokenPath);

            _cachedToken = hex;
        }

        // Plain string comparison — no timing-oracle mitigation needed at
        // localhost (LOCKED, v1.5 brief "Token auth").
        internal static bool IsValid(string presented) =>
            !string.IsNullOrEmpty(presented) && !string.IsNullOrEmpty(_cachedToken) &&
            string.Equals(presented, _cachedToken, StringComparison.Ordinal);
    }
}
