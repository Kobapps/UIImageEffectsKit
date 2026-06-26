#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;

namespace SDFImageKit.EditorTools
{
    /// <summary>
    /// Editor master switch for the whole package, mirroring <see cref="SDFImage.EffectsEnabled"/>.
    /// Toggle it from <b>Tools ▸ UI Image Effects Kit ▸ Effects Enabled</b>. The choice persists across
    /// editor sessions and play-in-editor (via EditorPrefs) and is applied on every domain reload.
    ///
    /// Builds aren't affected by this editor toggle — set <see cref="SDFImage.EffectsEnabled"/> from your
    /// own code if you want to flip the whole package on/off at runtime in a player.
    /// </summary>
    [InitializeOnLoad]
    public static class SDFGlobalToggle
    {
        private const string MenuPath = "Tools/UI Image Effects Kit/Effects Enabled";
        private const string PrefKey = "SDFImageKit.EffectsEnabled";

        static SDFGlobalToggle()
        {
            // Runs on editor load and on every domain reload (including entering Play mode), so the
            // editor's choice is applied before any SDFImage renders.
            SDFImage.EffectsEnabled = EditorPrefs.GetBool(PrefKey, true);
        }

        /// <summary>Set the package master switch and persist it (editor). Used by the menu and inspector.</summary>
        public static void SetEnabled(bool enabled)
        {
            SDFImage.EffectsEnabled = enabled;       // refreshes every live SDFImage
            EditorPrefs.SetBool(PrefKey, enabled);
            InternalEditorUtility.RepaintAllViews();
        }

        [MenuItem(MenuPath, false, 2050)]
        private static void Toggle() => SetEnabled(!SDFImage.EffectsEnabled);

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, SDFImage.EffectsEnabled);
            return true;
        }
    }
}
#endif
