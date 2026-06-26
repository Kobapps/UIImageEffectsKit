using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UI;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SDFImageKit.EditorTools
{
    /// <summary>
    /// Modern UI Toolkit inspector for <see cref="SDFImage"/>: the native Image fields, a bake
    /// section, and a Photoshop-style draggable effect stack. Effects can be added, removed,
    /// drag-reordered, toggled, and repeated (except the singleton Face, which is pinned to the
    /// front so the sprite always draws on top of its effects).
    /// </summary>
    [CustomEditor(typeof(SDFImage))]
    public class SDFImageEditor : ImageEditor
    {
        private SerializedProperty m_StackProp;
        private SerializedProperty m_BakedData;
        private SerializedProperty m_SdfMaterial;
        private SerializedProperty m_GenerateAtRuntime;
        private SerializedProperty m_RuntimeParams;

        private VisualElement m_StackContainer;
        private VisualElement m_DropLine;
        private VisualElement m_Ghost;       // floating preview of the dragged card
        private VisualElement m_DragCard;    // the source card being dragged (dimmed)
        private readonly List<VisualElement> m_Cards = new List<VisualElement>();
        private int m_DragFrom = -1;
        private bool m_FaceAtFront;

        private static readonly SDFEffectKind[] k_AddOrder =
        {
            SDFEffectKind.Outline, SDFEffectKind.Shadow, SDFEffectKind.Glow, SDFEffectKind.Face
        };

        private static bool Pro => EditorGUIUtility.isProSkin;
        private static Color CardBg => Pro ? new Color(1, 1, 1, 0.035f) : new Color(0, 0, 0, 0.03f);
        private static Color CardBgHover => Pro ? new Color(1, 1, 1, 0.07f) : new Color(0, 0, 0, 0.06f);
        private static Color CardBorder => new Color(0, 0, 0, 0.25f);
        private static Color Accent => new Color(0.40f, 0.62f, 1f);
        private static Color Muted => new Color(0.55f, 0.55f, 0.58f);

        protected override void OnEnable()
        {
            base.OnEnable();
            m_StackProp = serializedObject.FindProperty("m_Stack");
            m_BakedData = serializedObject.FindProperty("m_BakedData");
            m_SdfMaterial = serializedObject.FindProperty("m_SdfMaterial");
            m_GenerateAtRuntime = serializedObject.FindProperty("m_GenerateAtRuntime");
            m_RuntimeParams = serializedObject.FindProperty("m_RuntimeParams");
        }

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();
            root.style.marginTop = 2;

            // --- global master-switch notice (only shown when the package is turned off) ---
            root.Add(BuildGlobalToggleNotice());

            // --- Image (native, IMGUI) inside a foldout ---
            var imageFold = new Foldout { text = "Image", value = true };
            imageFold.style.unityFontStyleAndWeight = FontStyle.Bold;
            var imageBody = new IMGUIContainer(() =>
            {
                if (target == null) return;
                serializedObject.Update();
                base.OnInspectorGUI();
            });
            imageBody.style.unityFontStyleAndWeight = FontStyle.Normal;
            imageFold.Add(imageBody);
            root.Add(imageFold);

            root.Add(Spacer(8));
            root.Add(SectionLabel("SDF Field"));

            var bakeCard = Panel();
            bakeCard.Add(new PropertyField(m_BakedData, "Baked Field"));
            bakeCard.Add(BuildBakeControls());
            root.Add(bakeCard);

            root.Add(Spacer(8));

            // --- effect stack header ---
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 } };
            header.Add(new Label("Effect Stack") { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, flexGrow = 1 } });
            header.Add(BuildAddButton());
            root.Add(header);
            root.Add(new Label("Drag the ≡ handle to reorder. The ★ Face always stays in front.")
            { style = { color = Muted, fontSize = 10, marginBottom = 6, whiteSpace = WhiteSpace.Normal } });

            m_StackContainer = new VisualElement();
            m_DropLine = BuildDropLine();
            m_StackContainer.Add(m_DropLine);
            root.Add(m_StackContainer);
            RebuildStack();

            root.Add(Spacer(8));

            var rtFold = new Foldout { text = "Runtime Generation", value = false };
            rtFold.style.unityFontStyleAndWeight = FontStyle.Bold;
            var rtBody = new VisualElement { style = { unityFontStyleAndWeight = FontStyle.Normal } };
            rtBody.Add(new PropertyField(m_GenerateAtRuntime));
            rtBody.Add(new PropertyField(m_RuntimeParams));
            rtFold.Add(rtBody);
            root.Add(rtFold);

            root.TrackSerializedObjectValue(serializedObject, _ =>
            {
                foreach (var t in targets) (t as SDFImage)?.MarkEffectsDirty();
            });

            return root;
        }

        // Warning banner shown only while the package master switch is OFF (every SDFImage draws as a
        // plain Image). Includes a one-click re-enable. Hidden when effects are on.
        private VisualElement BuildGlobalToggleNotice()
        {
            var box = new VisualElement();

            void Refresh()
            {
                box.Clear();
                if (SDFImage.EffectsEnabled) return;

                box.Add(new HelpBox(
                    "Effects are globally OFF — every SDFImage is rendering as a plain Image. " +
                    "Toggle via Tools ▸ UI Image Effects Kit ▸ Effects Enabled.",
                    HelpBoxMessageType.Warning)
                { style = { marginBottom = 4 } });

                box.Add(new Button(() => { SDFGlobalToggle.SetEnabled(true); Refresh(); })
                { text = "Enable Effects" });
            }

            Refresh();
            // Re-evaluate when the inspector regains focus (the toggle may change from the menu).
            box.RegisterCallback<FocusInEvent>(_ => Refresh());
            return box;
        }

        // ====================================================================================
        // Effect stack
        // ====================================================================================

        private void RebuildStack()
        {
            // Keep the drop line; rebuild cards.
            m_StackContainer.Clear();
            m_StackContainer.Add(m_DropLine);
            m_Cards.Clear();
            serializedObject.Update();

            if (targets.Length > 1)
            {
                m_StackContainer.Add(new HelpBox("Effect stack editing is single-object only.", HelpBoxMessageType.Info));
                return;
            }

            int count = m_StackProp.arraySize;
            m_FaceAtFront = count > 0 && KindAt(0) == SDFEffectKind.Face;

            if (count == 0)
                m_StackContainer.Add(new HelpBox("Empty stack. Add a Face to show the sprite.", HelpBoxMessageType.None));

            for (int i = 0; i < count; i++)
            {
                var card = BuildCard(i);
                m_Cards.Add(card);
                m_StackContainer.Add(card);
            }

            m_StackContainer.Bind(serializedObject);
        }

        private VisualElement BuildCard(int index)
        {
            var element = m_StackProp.GetArrayElementAtIndex(index);
            var fx = element.managedReferenceValue as SDFEffect;
            bool isFace = fx != null && fx.Kind == SDFEffectKind.Face;

            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.marginBottom = 6;
            card.style.backgroundColor = CardBg;
            card.style.borderTopLeftRadius = card.style.borderTopRightRadius =
                card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 6;
            card.style.borderLeftWidth = card.style.borderRightWidth =
                card.style.borderTopWidth = card.style.borderBottomWidth = 1;
            card.style.borderLeftColor = card.style.borderRightColor =
                card.style.borderTopColor = card.style.borderBottomColor = CardBorder;
            card.style.overflow = Overflow.Hidden;
            card.RegisterCallback<MouseEnterEvent>(_ => card.style.backgroundColor = CardBgHover);
            card.RegisterCallback<MouseLeaveEvent>(_ => card.style.backgroundColor = CardBg);

            // accent bar
            var accent = new VisualElement { style = { width = 4, backgroundColor = AccentColor(fx?.Kind ?? SDFEffectKind.Outline) } };
            card.Add(accent);

            var content = new VisualElement { style = { flexGrow = 1, paddingLeft = 8, paddingRight = 6, paddingTop = 5, paddingBottom = 6 } };
            content.style.opacity = (fx != null && fx.enabled) ? 1f : 0.5f;
            card.Add(content);

            // header
            var head = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };

            if (isFace)
            {
                head.Add(new Label("★") { tooltip = "Face is pinned to the front", style = { color = Accent, fontSize = 13, marginRight = 6, width = 14 } });
            }
            else
            {
                var grip = new Label("≡") { tooltip = "Drag to reorder", style = { color = Muted, fontSize = 15, marginRight = 6, width = 14 } };
                AttachDrag(grip, index, card, fx);
                head.Add(grip);
            }

            var enabledToggle = new Toggle { bindingPath = element.FindPropertyRelative("enabled").propertyPath };
            enabledToggle.RegisterValueChangedCallback(ev => content.style.opacity = ev.newValue ? 1f : 0.5f);
            head.Add(enabledToggle);

            head.Add(new Label(fx != null ? fx.DisplayName : "Effect")
            { style = { unityFontStyleAndWeight = FontStyle.Bold, flexGrow = 1, marginLeft = 2 } });

            int captured = index;
            var remove = new Button(() => RemoveEffect(captured)) { text = "✕" };
            remove.tooltip = "Remove";
            remove.style.width = 20; remove.style.height = 18;
            remove.style.paddingLeft = remove.style.paddingRight = 0;
            remove.style.backgroundColor = Color.clear;
            remove.style.borderTopWidth = remove.style.borderBottomWidth =
                remove.style.borderLeftWidth = remove.style.borderRightWidth = 0;
            remove.style.color = Muted;
            head.Add(remove);

            content.Add(head);

            // body fields
            var body = new VisualElement { style = { marginTop = 4 } };
            AddElementFields(body, element);
            content.Add(body);

            return card;
        }

        private static void AddElementFields(VisualElement body, SerializedProperty element)
        {
            var it = element.Copy();
            var end = element.GetEndProperty();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (SerializedProperty.EqualContents(it, end)) break;
                if (it.name == "enabled") continue;
                body.Add(new PropertyField(it.Copy()));
            }
        }

        // ----- drag reordering --------------------------------------------------------------

        private void AttachDrag(VisualElement grip, int index, VisualElement card, SDFEffect fx)
        {
            grip.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                m_DragFrom = index;
                m_DragCard = card;
                grip.CapturePointer(e.pointerId);

                // Lift the source card and spawn a floating ghost that follows the cursor.
                card.style.opacity = 0.35f;
                m_Ghost = BuildGhost(fx, card.layout.width > 1 ? card.layout.width : m_StackContainer.layout.width);
                m_StackContainer.Add(m_Ghost);
                MoveGhost(e.position);
                ShowDropLine(ComputeDropTarget(e.position));
                e.StopPropagation();
            });
            grip.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (m_DragFrom < 0 || !grip.HasPointerCapture(e.pointerId)) return;
                MoveGhost(e.position);
                ShowDropLine(ComputeDropTarget(e.position));
                e.StopPropagation();
            });
            grip.RegisterCallback<PointerUpEvent>(e =>
            {
                if (m_DragFrom < 0) return;
                int from = m_DragFrom;
                int to = ComputeDropTarget(e.position);
                EndDrag();                       // clears m_DragFrom + removes ghost first
                grip.ReleasePointer(e.pointerId); // capture-out now sees no active drag
                ApplyMove(from, to);
                e.StopPropagation();
            });
            // Safety: if capture is lost (focus change, escape, etc.) without a PointerUp.
            grip.RegisterCallback<PointerCaptureOutEvent>(_ =>
            {
                if (m_DragFrom < 0) return;       // normal release already handled it
                EndDrag();
                RebuildStack();
            });
        }

        private void EndDrag()
        {
            HideDropLine();
            if (m_Ghost != null) { m_Ghost.RemoveFromHierarchy(); m_Ghost = null; }
            if (m_DragCard != null) { m_DragCard.style.opacity = 1f; m_DragCard = null; }
            m_DragFrom = -1;
        }

        private const float k_GhostHeight = 30f;

        private void MoveGhost(Vector2 worldPos)
        {
            if (m_Ghost == null) return;
            float y = m_StackContainer.WorldToLocal(worldPos).y;
            m_Ghost.style.top = y - k_GhostHeight * 0.5f;
        }

        private VisualElement BuildGhost(SDFEffect fx, float width)
        {
            var g = new VisualElement();
            g.pickingMode = PickingMode.Ignore;
            g.style.position = Position.Absolute;
            g.style.left = 0;
            g.style.width = Mathf.Max(40f, width);
            g.style.height = k_GhostHeight;
            g.style.flexDirection = FlexDirection.Row;
            g.style.alignItems = Align.Center;
            g.style.opacity = 0.96f;
            g.style.backgroundColor = Pro ? new Color(0.24f, 0.24f, 0.27f) : new Color(0.86f, 0.86f, 0.89f);
            g.style.borderTopLeftRadius = g.style.borderTopRightRadius =
                g.style.borderBottomLeftRadius = g.style.borderBottomRightRadius = 6;
            g.style.borderLeftWidth = g.style.borderRightWidth =
                g.style.borderTopWidth = g.style.borderBottomWidth = 1;
            var ac = AccentColor(fx?.Kind ?? SDFEffectKind.Outline);
            g.style.borderLeftColor = g.style.borderRightColor =
                g.style.borderTopColor = g.style.borderBottomColor = ac;
            g.style.overflow = Overflow.Hidden;

            g.Add(new VisualElement { style = { width = 4, height = Length.Percent(100), backgroundColor = ac } });
            g.Add(new Label("≡  " + (fx != null ? fx.DisplayName : "Effect"))
            { style = { unityFontStyleAndWeight = FontStyle.Bold, marginLeft = 8, color = Pro ? Color.white : Color.black } });
            return g;
        }

        private int ComputeDropTarget(Vector2 worldPos)
        {
            float y = m_StackContainer.WorldToLocal(worldPos).y;
            int target = m_Cards.Count;
            for (int k = 0; k < m_Cards.Count; k++)
            {
                var c = m_Cards[k];
                if (y < c.layout.yMin + c.layout.height * 0.5f) { target = k; break; }
            }
            if (m_FaceAtFront && target < 1) target = 1; // never above the Face
            return target;
        }

        private void ShowDropLine(int target)
        {
            float y;
            if (m_Cards.Count == 0) y = 0;
            else if (target <= 0) y = m_Cards[0].layout.yMin;
            else if (target >= m_Cards.Count) y = m_Cards[m_Cards.Count - 1].layout.yMax;
            else y = m_Cards[target].layout.yMin;

            m_DropLine.style.top = y - 1;
            m_DropLine.style.display = DisplayStyle.Flex;
        }

        private void HideDropLine() => m_DropLine.style.display = DisplayStyle.None;

        private void ApplyMove(int from, int insert)
        {
            int n = m_StackProp.arraySize;
            int to = insert > from ? insert - 1 : insert;   // removal shifts indices
            to = Mathf.Clamp(to, 0, n - 1);
            if (to != from)
            {
                serializedObject.Update();
                m_StackProp.MoveArrayElement(from, to);
                serializedObject.ApplyModifiedProperties();
            }
            PinFaceToFront();
            (target as SDFImage)?.MarkEffectsDirty();
            RebuildStack();
        }

        // ----- add / remove / pin -----------------------------------------------------------

        private void AddEffect(SDFEffectKind kind)
        {
            serializedObject.Update();
            m_StackProp.InsertArrayElementAtIndex(0);
            m_StackProp.GetArrayElementAtIndex(0).managedReferenceValue = SDFEffectFactory.Create(kind);
            serializedObject.ApplyModifiedProperties();
            PinFaceToFront();                 // new effect lands just behind the Face
            (target as SDFImage)?.MarkEffectsDirty();
            RebuildStack();
        }

        private void RemoveEffect(int index)
        {
            serializedObject.Update();
            m_StackProp.DeleteArrayElementAtIndex(index);
            serializedObject.ApplyModifiedProperties();
            PinFaceToFront();
            (target as SDFImage)?.MarkEffectsDirty();
            RebuildStack();
        }

        /// <summary>Guarantee the Face (if any) is at index 0 so the sprite renders in front.</summary>
        private void PinFaceToFront()
        {
            serializedObject.Update();
            int faceIdx = -1;
            for (int i = 0; i < m_StackProp.arraySize; i++)
            {
                if (KindAt(i) == SDFEffectKind.Face) { faceIdx = i; break; }
            }
            if (faceIdx > 0)
            {
                m_StackProp.MoveArrayElement(faceIdx, 0);
                serializedObject.ApplyModifiedProperties();
            }
        }

        private SDFEffectKind KindAt(int i)
        {
            var fx = m_StackProp.GetArrayElementAtIndex(i).managedReferenceValue as SDFEffect;
            return fx != null ? fx.Kind : SDFEffectKind.Outline;
        }

        private void ShowAddMenu()
        {
            var present = new HashSet<SDFEffectKind>();
            for (int i = 0; i < m_StackProp.arraySize; i++)
                if (m_StackProp.GetArrayElementAtIndex(i).managedReferenceValue is SDFEffect fx) present.Add(fx.Kind);

            var menu = new GenericMenu();
            foreach (var kind in k_AddOrder)
            {
                var label = new GUIContent(KindLabel(kind));
                bool canAdd = SDFEffectFactory.AllowsMultiple(kind) || !present.Contains(kind);
                if (canAdd) { var c = kind; menu.AddItem(label, false, () => AddEffect(c)); }
                else menu.AddDisabledItem(label);
            }
            menu.ShowAsContext();
        }

        // ====================================================================================
        // Bake controls
        // ====================================================================================

        private VisualElement BuildBakeControls()
        {
            var box = new VisualElement();
            var img = (SDFImage)target;
            var sprite = img.overrideSprite != null ? img.overrideSprite : img.sprite;
            var tex = sprite != null ? sprite.texture as Texture2D : null;

            // Auto-resolve a field already embedded in the texture.
            if (m_BakedData.objectReferenceValue == null && tex != null)
            {
                var embedded = SDFTextureEmbedder.FindEmbedded(tex);
                if (embedded != null)
                {
                    serializedObject.Update();
                    m_BakedData.objectReferenceValue = embedded;
                    serializedObject.ApplyModifiedProperties();
                    img.ForceResolve();
                }
            }

            bool isEmbedded = tex != null && SDFTextureEmbedder.IsEnabled(AssetDatabase.GetAssetPath(tex));

            var embed = new Button(EmbedForTarget)
            { text = isEmbedded ? "Re-embed SDF in Texture" : "Embed SDF in Texture" };
            embed.tooltip = "Bakes the field as sub-assets inside the texture (no extra file).";
            embed.style.marginTop = 4; embed.style.height = 22;
            embed.SetEnabled(tex != null);
            box.Add(embed);

            if (isEmbedded)
            {
                var remove = new Button(RemoveEmbedForTarget) { text = "Remove embedded SDF" };
                box.Add(remove);
            }

            if (tex == null)
                box.Add(new HelpBox("Assign a sprite to embed or generate its SDF.", HelpBoxMessageType.Info));
            else if (!isEmbedded && m_BakedData.objectReferenceValue == null)
                box.Add(new HelpBox("No field yet. It is generated at runtime if 'Generate At Runtime' is on, " +
                    "or click Embed to bake it into the texture (zero runtime cost). Embedding also sets " +
                    "the sprite to Full Rect so effects have room.", HelpBoxMessageType.None));

            return box;
        }

        private void EmbedForTarget()
        {
            var img = (SDFImage)target;
            var sprite = img.overrideSprite != null ? img.overrideSprite : img.sprite;
            if (sprite == null || sprite.texture == null) return;

            var existing = SDFTextureEmbedder.FindEmbedded(sprite.texture);
            SDFGenerationParams? p = existing != null ? existing.bakeParams : (SDFGenerationParams?)null;
            var data = SDFTextureEmbedder.Embed((Texture2D)sprite.texture, p);
            if (data != null)
            {
                serializedObject.Update();
                m_BakedData.objectReferenceValue = data;
                serializedObject.ApplyModifiedProperties();
                img.ForceResolve();
            }
        }

        private void RemoveEmbedForTarget()
        {
            var img = (SDFImage)target;
            var sprite = img.overrideSprite != null ? img.overrideSprite : img.sprite;
            if (sprite == null || sprite.texture == null) return;

            SDFTextureEmbedder.Remove((Texture2D)sprite.texture);
            serializedObject.Update();
            m_BakedData.objectReferenceValue = null;
            serializedObject.ApplyModifiedProperties();
            img.ForceResolve();
        }

        // ====================================================================================
        // Style helpers
        // ====================================================================================

        private Button BuildAddButton()
        {
            var b = new Button(ShowAddMenu) { text = "+  Add Effect" };
            b.style.height = 22;
            b.style.paddingLeft = b.style.paddingRight = 10;
            b.style.borderTopLeftRadius = b.style.borderTopRightRadius =
                b.style.borderBottomLeftRadius = b.style.borderBottomRightRadius = 4;
            b.style.backgroundColor = new Color(Accent.r, Accent.g, Accent.b, 0.22f);
            b.style.unityFontStyleAndWeight = FontStyle.Bold;
            return b;
        }

        private VisualElement BuildDropLine()
        {
            var line = new VisualElement();
            line.style.position = Position.Absolute;
            line.style.left = 0; line.style.right = 0; line.style.height = 2;
            line.style.backgroundColor = Accent;
            line.style.display = DisplayStyle.None;
            return line;
        }

        private static Color AccentColor(SDFEffectKind kind)
        {
            switch (kind)
            {
                case SDFEffectKind.Face: return new Color(0.55f, 0.55f, 0.6f);
                case SDFEffectKind.Outline: return new Color(0.40f, 0.62f, 1f);
                case SDFEffectKind.Shadow: return new Color(0.62f, 0.45f, 0.9f);
                case SDFEffectKind.Glow: return new Color(0.25f, 0.85f, 0.95f);
                default: return new Color(0.5f, 0.5f, 0.5f);
            }
        }

        private static string KindLabel(SDFEffectKind kind) =>
            kind == SDFEffectKind.Shadow ? "Shadow / Underlay" : kind.ToString();

        private static VisualElement Spacer(float h) => new VisualElement { style = { height = h } };

        private static Label SectionLabel(string text) => new Label(text)
        { style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 12, marginBottom = 3 } };

        private static VisualElement Panel()
        {
            var v = new VisualElement();
            v.style.paddingLeft = v.style.paddingRight = 8;
            v.style.paddingTop = v.style.paddingBottom = 6;
            v.style.backgroundColor = CardBg;
            v.style.borderTopLeftRadius = v.style.borderTopRightRadius =
                v.style.borderBottomLeftRadius = v.style.borderBottomRightRadius = 6;
            v.style.borderLeftWidth = v.style.borderRightWidth =
                v.style.borderTopWidth = v.style.borderBottomWidth = 1;
            v.style.borderLeftColor = v.style.borderRightColor =
                v.style.borderTopColor = v.style.borderBottomColor = CardBorder;
            return v;
        }
    }
}
