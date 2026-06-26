#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using SDFImageKit;
using SDFImageKit.EditorTools;

namespace SDFImageKit.Samples
{
    /// <summary>
    /// Builds a showcase of SDFImage effect stacks in the active scene using the sample sprites.
    /// Run via Tools ▸ UI Image Effects Kit ▸ Build Effect Showcase.
    /// </summary>
    public static class SDFExampleBuilder
    {
        private static Font s_Font;

        [MenuItem("Tools/UI Image Effects Kit/Build Effect Showcase")]
        public static void Build()
        {
            s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Version-safe single-object lookup (FindObjectOfType is obsolete in newer Unity).
#if UNITY_2023_1_OR_NEWER
            var canvas = Object.FindFirstObjectByType<Canvas>();
#else
            var canvas = Object.FindObjectOfType<Canvas>();
#endif
            if (canvas == null)
            {
                var cgo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvas = cgo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }

            // Clean a previous showcase root (only our own).
            DestroyChild(canvas.transform, "SDF Examples");

            var rootGO = new GameObject("SDF Examples", typeof(RectTransform));
            var root = (RectTransform)rootGO.transform;
            root.SetParent(canvas.transform, false);
            Stretch(root);

            // Dark background so light outlines/glows read clearly.
            var bg = NewUI("Background", root);
            Stretch(bg.rectTransform);
            bg.color = new Color(0.12f, 0.12f, 0.15f, 1f);

            Title(root, "UI Image Effects Kit — Effect Stacks", 40);

            var leaf = Load("leaf_t");
            var bag = Load("bag_t");
            var hammer = Load("hammer_t");
            var dinner = Load("dinner_t");
            var axe = Load("sl_axe_t");

            if (leaf == null)
            {
                Debug.LogWarning("[UI Image Effects Kit] Sample sprites not found. Import the " +
                                 "'Effect Showcase' sample from the Package Manager first.");
                return;
            }

            var examples = new (Sprite sprite, string label, List<SDFEffect> fx)[]
            {
                (leaf, "Plain", Stack(Face())),
                (leaf, "Outline", Stack(Face(), Outline(Color.white, 0.35f))),
                (bag, "Double Outline", Stack(Face(),
                    Outline(new Color(1f, 0.85f, 0.2f), 0.18f), Outline(Color.black, 0.42f))),
                (hammer, "Drop Shadow", Stack(Face(),
                    Shadow(new Color(0, 0, 0, 0.65f), new Vector2(0.05f, -0.08f), 0.12f))),
                (dinner, "Glow", Stack(Face(), Glow(new Color(1f, 0.55f, 0.1f), 1.0f, 1.6f))),
                (axe, "Combined", Stack(Face(),
                    Outline(Color.white, 0.22f),
                    Shadow(new Color(0, 0, 0, 0.6f), new Vector2(0.04f, -0.06f), 0.1f),
                    Glow(new Color(0.2f, 0.8f, 1f), 0.8f, 1.5f))),
                (leaf, "Crisp Face + Outline", Stack(FaceSilhouette(0.1f), Outline(Color.black, 0.3f))),
                (bag, "Multi-Glow Neon", Stack(Face(), Outline(Color.white, 0.18f),
                    Glow(new Color(1f, 0.2f, 0.8f), 0.6f, 2.0f), Glow(new Color(0.2f, 0.9f, 1f), 1.3f, 1.4f))),
            };

            // 2 columns x 4 rows.
            const float cellW = 185f, cellH = 150f;
            for (int i = 0; i < examples.Length; i++)
            {
                int col = i % 2, row = i / 2;
                float x = (col - 0.5f) * cellW;
                float y = (1.5f - row) * cellH - 36f;
                MakeCell(root, examples[i].sprite, examples[i].label, examples[i].fx, new Vector2(x, y));
            }

            EditorUtility.SetDirty(rootGO);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(canvas.gameObject.scene);
            Selection.activeGameObject = rootGO;
            Debug.Log("[UI Image Effects Kit] Built " + examples.Length + " effect-stack examples.");
        }

        // ---- effect helpers ----
        private static List<SDFEffect> Stack(params SDFEffect[] fx) => new List<SDFEffect>(fx);
        private static SDFFaceEffect Face() => new SDFFaceEffect();
        private static SDFFaceEffect FaceSilhouette(float dilate) =>
            new SDFFaceEffect { mode = SDFFaceEffect.FaceMode.Silhouette, dilate = dilate };
        private static SDFOutlineEffect Outline(Color c, float w) => new SDFOutlineEffect { color = c, width = w };
        private static SDFShadowEffect Shadow(Color c, Vector2 o, float s) =>
            new SDFShadowEffect { color = c, offset = o, softness = s };
        private static SDFGlowEffect Glow(Color c, float w, float p) =>
            new SDFGlowEffect { color = c, width = w, power = p };

        // ---- scene helpers ----
        private static void MakeCell(Transform parent, Sprite sprite, string label, List<SDFEffect> fx, Vector2 pos)
        {
            var cell = new GameObject("Cell_" + label, typeof(RectTransform));
            var crt = (RectTransform)cell.transform;
            crt.SetParent(parent, false);
            crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f);
            crt.anchoredPosition = pos;
            crt.sizeDelta = new Vector2(170f, 140f);

            var imgGO = new GameObject("SDFImage", typeof(RectTransform), typeof(SDFImage));
            var irt = (RectTransform)imgGO.transform;
            irt.SetParent(crt, false);
            irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
            irt.anchoredPosition = new Vector2(0, 18f);
            irt.sizeDelta = new Vector2(96f, 96f);

            var sdf = imgGO.GetComponent<SDFImage>();
            sdf.sprite = sprite;
            sdf.effects.Clear();
            sdf.effects.AddRange(fx);
            if (sprite != null && sprite.texture != null)
            {
                // Embed the field as sub-assets in the texture (once per unique texture).
                var data = SDFTextureEmbedder.FindEmbedded(sprite.texture)
                           ?? SDFTextureEmbedder.Embed((Texture2D)sprite.texture);
                sdf.bakedData = data;
            }
            sdf.ForceResolve();

            var txtGO = new GameObject("Label", typeof(RectTransform), typeof(Text));
            var trt = (RectTransform)txtGO.transform;
            trt.SetParent(crt, false);
            trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
            trt.anchoredPosition = new Vector2(0, -54f);
            trt.sizeDelta = new Vector2(180f, 24f);
            var txt = txtGO.GetComponent<Text>();
            txt.text = label;
            txt.font = s_Font;
            txt.fontSize = 15;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.color = Color.white;
        }

        private static Image NewUI(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            return go.GetComponent<Image>();
        }

        private static void Title(Transform parent, string text, float topOffset)
        {
            var go = new GameObject("Title", typeof(RectTransform), typeof(Text));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -topOffset + 36f);
            rt.sizeDelta = new Vector2(1000f, 40f);
            var t = go.GetComponent<Text>();
            t.text = text; t.font = s_Font; t.fontSize = 26;
            t.alignment = TextAnchor.MiddleCenter; t.color = Color.white;
            t.fontStyle = FontStyle.Bold;
        }

        /// <summary>Find a sample sprite by file name, wherever the user imported the sample.</summary>
        private static Sprite Load(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets(name + " t:Sprite"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == name)
                {
                    var sp = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                    if (sp != null) return sp;
                }
            }
            return null;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private static void DestroyChild(Transform parent, string name)
        {
            var t = parent.Find(name);
            if (t != null) Object.DestroyImmediate(t.gameObject);
        }
    }
}
#endif
