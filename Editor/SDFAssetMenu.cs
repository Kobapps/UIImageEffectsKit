using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SDFImageKit.EditorTools
{
    /// <summary>
    /// Right-click context menu on textures: embed / re-embed / remove the SDF field. The field is
    /// stored as sub-assets inside the texture itself (no companion file).
    /// </summary>
    public static class SDFAssetMenu
    {
        private const string EmbedPath = "Assets/UI Image Effects Kit/Embed SDF in Texture";
        private const string RemovePath = "Assets/UI Image Effects Kit/Remove Embedded SDF";

        [MenuItem(EmbedPath, false, 2000)]
        private static void Embed()
        {
            foreach (var tex in SelectedTextures())
            {
                var existing = SDFTextureEmbedder.FindEmbedded(tex);
                SDFGenerationParams? p = existing != null ? existing.bakeParams : (SDFGenerationParams?)null;
                SDFTextureEmbedder.Embed(tex, p);
            }
        }

        [MenuItem(RemovePath, false, 2001)]
        private static void Remove()
        {
            foreach (var tex in SelectedTextures())
                SDFTextureEmbedder.Remove(tex);
        }

        [MenuItem(EmbedPath, true)]
        [MenuItem(RemovePath, true)]
        private static bool Validate()
        {
            foreach (var _ in SelectedTextures()) return true;
            return false;
        }

        private static IEnumerable<Texture2D> SelectedTextures()
        {
            foreach (var obj in Selection.objects)
            {
                if (obj is Texture2D tex)
                {
                    string path = AssetDatabase.GetAssetPath(tex);
                    if (AssetImporter.GetAtPath(path) is TextureImporter)
                        yield return tex;
                }
            }
        }
    }
}
