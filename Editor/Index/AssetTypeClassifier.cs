using System.IO;

namespace UnityBridge.Editor
{
    // Friendly `type` strings for /query and assets.jsonl. Not LOCKED by the
    // task brief beyond "prefab" needing to work (verification 2.1, 2.5) —
    // this is a v1 classification choice, documented in /help/query and the
    // package README's DECISIONS heading rather than assumed silently.
    internal static class AssetTypeClassifier
    {
        internal static string Classify(string assetPath, bool isDirectory)
        {
            if (isDirectory) return "folder";

            string ext = Path.GetExtension(assetPath).ToLowerInvariant();
            switch (ext)
            {
                case ".prefab": return "prefab";
                case ".unity": return "scene";
                case ".cs": return "script";
                case ".mat": return "material";
                case ".asset": return "scriptableobject";
                case ".shader": case ".shadergraph": case ".compute": return "shader";
                case ".anim": return "animationclip";
                case ".controller": return "animatorcontroller";
                case ".png": case ".jpg": case ".jpeg": case ".tga": case ".psd": case ".exr": case ".tiff": case ".bmp":
                    return "texture";
                case ".wav": case ".mp3": case ".ogg": case ".aiff": case ".flac":
                    return "audioclip";
                case ".fbx": case ".obj": case ".blend": case ".dae":
                    return "model";
                case ".physicmaterial": case ".physicsmaterial2d":
                    return "physicsmaterial";
                default:
                    return "other";
            }
        }
    }
}
