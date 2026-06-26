using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SDFImageKit.EditorTools
{
    /// <summary>
    /// Creation + conversion helpers: a "GameObject/UI/SDF Image" entry and a component context
    /// menu to convert an existing <see cref="Image"/> into an <see cref="SDFImage"/> in place.
    /// </summary>
    public static class SDFImageMenu
    {
        [MenuItem("GameObject/UI/SDF Image", false, 2002)]
        private static void CreateSDFImage(MenuCommand menuCommand)
        {
            var parent = ResolveUIParent(menuCommand);

            var go = new GameObject("SDF Image", typeof(RectTransform), typeof(SDFImage));
            GameObjectUtility.SetParentAndAlign(go, parent);
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(100f, 100f);

            var sdf = go.GetComponent<SDFImage>();
            if (sdf.effects.Count == 0) sdf.effects.Add(new SDFFaceEffect());

            Undo.RegisterCreatedObjectUndo(go, "Create SDF Image");
            Selection.activeGameObject = go;
        }

        /// <summary>Find a sensible UI parent (Canvas), creating a Canvas + EventSystem if needed.</summary>
        private static GameObject ResolveUIParent(MenuCommand menuCommand)
        {
            var context = menuCommand.context as GameObject;
            if (context != null)
            {
                var c = context.GetComponentInParent<Canvas>();
                if (c != null) return context;
            }

            var canvas = FindFirst<Canvas>();
            if (canvas == null)
            {
                var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                canvasGo.layer = LayerMask.NameToLayer("UI");
                Undo.RegisterCreatedObjectUndo(canvasGo, "Create Canvas");
                canvas = canvasGo.GetComponent<Canvas>();

                if (FindFirst<EventSystem>() == null)
                {
                    var es = new GameObject("EventSystem", typeof(EventSystem));
                    Undo.RegisterCreatedObjectUndo(es, "Create EventSystem");
                }
            }
            return canvas.gameObject;
        }

        // Version-safe single-object lookup (FindObjectOfType is obsolete in newer Unity).
        private static T FindFirst<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<T>();
#else
            return Object.FindObjectOfType<T>();
#endif
        }

        // ----- Convert existing Image -------------------------------------------------------

        [MenuItem("CONTEXT/Image/Convert to SDF Image")]
        private static void ConvertToSDFImage(MenuCommand command)
        {
            var image = command.context as Image;
            if (image == null || image is SDFImage) return;

            var go = image.gameObject;

            // Snapshot the fields worth carrying over.
            var sprite = image.sprite;
            var color = image.color;
            var material = image.material;
            var raycastTarget = image.raycastTarget;
            var maskable = image.maskable;
            var type = image.type;
            var preserveAspect = image.preserveAspect;
            var fillCenter = image.fillCenter;
            var fillMethod = image.fillMethod;
            var fillAmount = image.fillAmount;
            var fillClockwise = image.fillClockwise;
            var fillOrigin = image.fillOrigin;
            var ppu = image.pixelsPerUnitMultiplier;

            Undo.DestroyObjectImmediate(image);
            var sdf = Undo.AddComponent<SDFImage>(go);

            sdf.sprite = sprite;
            sdf.color = color;
            if (material != null && material.name != "Default UI Material") sdf.material = material;
            sdf.raycastTarget = raycastTarget;
            sdf.maskable = maskable;
            sdf.type = type;
            sdf.preserveAspect = preserveAspect;
            sdf.fillCenter = fillCenter;
            sdf.fillMethod = fillMethod;
            sdf.fillAmount = fillAmount;
            sdf.fillClockwise = fillClockwise;
            sdf.fillOrigin = fillOrigin;
            sdf.pixelsPerUnitMultiplier = ppu;

            if (sdf.effects.Count == 0) sdf.effects.Add(new SDFFaceEffect());

            // Auto-resolve a field already embedded in the sprite's texture.
            if (sprite != null && sprite.texture != null)
            {
                var data = SDFTextureEmbedder.FindEmbedded(sprite.texture);
                if (data != null) sdf.bakedData = data;
            }

            EditorUtility.SetDirty(go);
        }

        [MenuItem("CONTEXT/Image/Convert to SDF Image", true)]
        private static bool ConvertValidate(MenuCommand command)
        {
            return command.context is Image image && !(image is SDFImage);
        }
    }
}
