using System;
using UnityEditor;
using UnityEngine;

namespace SDFImageKit.EditorTools
{
    /// <summary>
    /// Per-texture SDF import settings, stored inside the texture importer's <c>userData</c> so they
    /// travel with the asset. A small marker keeps any other tool's userData intact.
    /// </summary>
    [Serializable]
    public class SDFImportSettings
    {
        public bool enabled;
        public SDFGenerationParams parameters = SDFGenerationParams.Default;

        private const string Marker = "@SDFKIT@";

        public static SDFImportSettings Read(AssetImporter importer)
        {
            var s = new SDFImportSettings { parameters = SDFGenerationParams.Default };
            if (importer == null) return s;
            string ud = importer.userData ?? string.Empty;
            int idx = ud.IndexOf(Marker, StringComparison.Ordinal);
            if (idx < 0) return s;
            try { JsonUtility.FromJsonOverwrite(ud.Substring(idx + Marker.Length), s); } catch { }
            return s;
        }

        public void Write(AssetImporter importer)
        {
            if (importer == null) return;
            string ud = importer.userData ?? string.Empty;
            int idx = ud.IndexOf(Marker, StringComparison.Ordinal);
            string prefix = idx >= 0 ? ud.Substring(0, idx) : ud;
            importer.userData = prefix + Marker + JsonUtility.ToJson(this);
        }
    }

    /// <summary>
    /// Bakes the signed distance field <b>into the texture asset itself</b> as nested sub-assets,
    /// during the texture's own import. Because the sub-assets are re-added on every import they
    /// persist across reimports (no companion file, no reimport loop), and they're regenerated only
    /// when the texture actually re-imports — i.e. when its source or import settings change.
    ///
    /// Uses the CPU 8SSEDT path (no GPU during import, so it works on build machines too).
    /// </summary>
    public class SDFTextureEmbedder : AssetPostprocessor
    {
        public const string FieldId = "SDFImageKit_Field";
        public const string DataId = "SDFImageKit_Data";

        private void OnPostprocessTexture(Texture2D texture)
        {
            var settings = SDFImportSettings.Read(assetImporter);
            if (!settings.enabled || texture == null) return;

            // The texture handed to a postprocessor is always pixel-readable here, regardless of the
            // final Read/Write setting — so sample it directly (no blit, no GPU).
            var pixels = texture.GetPixels32();
            var field = SDFGeneratorCPU.Generate(pixels, texture.width, texture.height,
                settings.parameters, out var padNorm);
            field.name = texture.name + " SDF Field";

            var data = ScriptableObject.CreateInstance<SDFData>();
            data.name = texture.name + " SDF";
            data.field = field;
            data.spread = Mathf.Clamp(settings.parameters.spread, 0.001f, 1f);
            data.sourceSize = new Vector2Int(texture.width, texture.height);
            data.padding = padNorm;
            data.bakeParams = settings.parameters;
            data.sourceTextureGuid = AssetDatabase.AssetPathToGUID(assetPath);

            // Hide the generated objects from the Project browser so they don't clutter the texture
            // and — crucially — don't get promoted to the asset's main object (which would make the
            // inspector preview/icon show the SDF). Hidden objects are never chosen as main, so the
            // texture stays the main asset. They're still referenceable (LoadAllAssetsAtPath finds
            // hidden objects) and survive reimports thanks to the stable identifiers below.
            field.hideFlags = HideFlags.HideInHierarchy;
            data.hideFlags = HideFlags.HideInHierarchy;

            context.AddObjectToAsset(FieldId, field);
            context.AddObjectToAsset(DataId, data);
        }

        // ====================================================================================
        // Editor API
        // ====================================================================================

        /// <summary>True when this texture has SDF embedding turned on.</summary>
        public static bool IsEnabled(string texturePath)
        {
            return SDFImportSettings.Read(AssetImporter.GetAtPath(texturePath)).enabled;
        }

        /// <summary>Find the SDF data already embedded in a texture asset, if any.</summary>
        public static SDFData FindEmbedded(Texture texture)
        {
            if (texture == null) return null;
            string path = AssetDatabase.GetAssetPath(texture);
            return FindEmbedded(path);
        }

        public static SDFData FindEmbedded(string texturePath)
        {
            if (string.IsNullOrEmpty(texturePath)) return null;
            foreach (var a in AssetDatabase.LoadAllAssetsAtPath(texturePath))
                if (a is SDFData d && d.IsValid) return d;
            return null;
        }

        /// <summary>
        /// Turn on SDF embedding for a texture (also forcing Full Rect mesh so effects have room),
        /// reimport, and return the embedded <see cref="SDFData"/> sub-asset.
        /// </summary>
        public static SDFData Embed(Texture2D texture, SDFGenerationParams? p = null)
        {
            if (texture == null) return null;
            string path = AssetDatabase.GetAssetPath(texture);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null)
            {
                Debug.LogWarning("[SDFImageKit] Can't embed: not a TextureImporter asset.");
                return null;
            }

            // Effects draw around the sprite, which needs a Full Rect mesh.
            if (ti.textureType == TextureImporterType.Sprite)
            {
                var tis = new TextureImporterSettings();
                ti.ReadTextureSettings(tis);
                if (tis.spriteMeshType != SpriteMeshType.FullRect)
                {
                    tis.spriteMeshType = SpriteMeshType.FullRect;
                    ti.SetTextureSettings(tis);
                }
            }

            var s = SDFImportSettings.Read(ti);
            s.enabled = true;
            if (p.HasValue) s.parameters = p.Value;
            s.Write(ti);
            ti.SaveAndReimport();   // one reimport runs OnPostprocessTexture, embedding the field

            return FindEmbedded(path);
        }

        /// <summary>Turn off embedding and reimport, removing the SDF sub-assets.</summary>
        public static void Remove(Texture2D texture)
        {
            if (texture == null) return;
            string path = AssetDatabase.GetAssetPath(texture);
            var ti = AssetImporter.GetAtPath(path);
            if (ti == null) return;
            var s = SDFImportSettings.Read(ti);
            if (!s.enabled) return;
            s.enabled = false;
            s.Write(ti);
            ti.SaveAndReimport();
        }
    }
}
