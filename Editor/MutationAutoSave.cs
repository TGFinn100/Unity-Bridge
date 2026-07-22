using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityBridge.Editor
{
    // v2 (LOCKED, task brief "Auto-save"): default-on save-after-mutation,
    // toggle reachable ONLY via a Unity Editor menu item — deliberately
    // outside Claude's own reach (no /act route reads or writes this value),
    // so it's a genuine human safeguard rather than something the same
    // token-authed caller could flip back on.
    internal static class MutationAutoSave
    {
        const string PrefKey = "UnityBridge.MutationAutoSave";
        const string MenuPath = "Tools/Unity Bridge/Auto-Save Mutations";

        internal static bool Enabled => EditorPrefs.GetBool(PrefKey, true);

        [MenuItem(MenuPath)]
        static void ToggleMenuItem()
        {
            EditorPrefs.SetBool(PrefKey, !Enabled);
        }

        // Standard Unity idiom: the validate function runs right before the
        // menu is shown, so this is where the checkmark actually gets set —
        // it's not just an enabled/disabled gate here.
        [MenuItem(MenuPath, true)]
        static bool ToggleMenuItemValidate()
        {
            Menu.SetChecked(MenuPath, Enabled);
            return true;
        }

        // Called after a mutation's Undo registration (LOCKED ordering, per
        // the brief's Undo table — Undo.Record*/RegisterCreatedObjectUndo
        // etc. always runs first). Returns whether the save actually
        // happened (== Enabled at call time), for the response's
        // "autoSaved" field — so a caller always knows whether a change is
        // durable yet without a separate query.
        internal static bool SaveIfEnabled(GameObject go) => SaveIfEnabled(go.scene);

        // Delete needs the scene captured before the object is destroyed —
        // there's no live GameObject left to read .scene from afterward.
        internal static bool SaveIfEnabled(Scene scene)
        {
            if (!Enabled) return false;
            if (!scene.IsValid()) return false; // defensive only — every mutation target here is a real scene object
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            return true;
        }
    }
}
