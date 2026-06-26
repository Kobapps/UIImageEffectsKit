using UnityEditor;
using UnityEngine;

namespace SDFImageKit.EditorTools
{
    /// <summary>
    /// Inspector + thumbnail for baked <see cref="SDFData"/> companion assets so you can eyeball
    /// the field without writing any code.
    /// </summary>
    [CustomEditor(typeof(SDFData))]
    public class SDFDataEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var data = (SDFData)target;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Field", data.field, typeof(Texture2D), false);
                EditorGUILayout.FloatField("Spread (fraction of sprite)", data.spread);
                EditorGUILayout.Vector2IntField("Source Size", data.sourceSize);
                EditorGUILayout.TextField("Source GUID", data.sourceTextureGuid);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bake Parameters", EditorStyles.boldLabel);
            var p = data.bakeParams;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.FloatField("Resolution Scale", p.resolutionScale);
                EditorGUILayout.FloatField("Spread", p.spread);
                EditorGUILayout.FloatField("Alpha Threshold", p.alphaThreshold);
                EditorGUILayout.EnumPopup("Backend", p.backend);
            }
        }

        public override bool HasPreviewGUI() => ((SDFData)target).field != null;

        public override void OnPreviewGUI(Rect r, GUIStyle background)
        {
            var data = (SDFData)target;
            if (data.field != null)
                EditorGUI.DrawTextureTransparent(r, data.field, ScaleMode.ScaleToFit);
        }
    }
}
