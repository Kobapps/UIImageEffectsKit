using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace SDFImageKit.EditorTools
{
    /// <summary>
    /// Guarantees the "UI/SDF Image" shader is in the project's Always Included Shaders list so
    /// <see cref="Shader.Find"/> resolves it in player builds (where it may otherwise be stripped,
    /// since the runtime creates its material from code rather than a referenced .mat).
    /// </summary>
    [InitializeOnLoad]
    public static class SDFShaderInclude
    {
        static SDFShaderInclude()
        {
            // Defer until the asset database is ready.
            EditorApplication.delayCall += EnsureIncluded;
        }

        private static void EnsureIncluded()
        {
            var shader = Shader.Find(SDFShaderIDs.ShaderName);
            if (shader == null) return;

            var gs = GraphicsSettings.GetGraphicsSettings();
            var so = new SerializedObject(gs);
            var arr = so.FindProperty("m_AlwaysIncludedShaders");
            if (arr == null) return;

            for (int i = 0; i < arr.arraySize; i++)
            {
                if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                    return; // already present
            }

            int idx = arr.arraySize;
            arr.InsertArrayElementAtIndex(idx);
            arr.GetArrayElementAtIndex(idx).objectReferenceValue = shader;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
