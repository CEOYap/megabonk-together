using UnityEngine;
using UnityEngine.UI;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// Carries a game button's serialized fields across the <c>MyButtonNormal</c> swap. Without
    /// them, <c>SetColor</c> throws on a null <c>background</c> every hover
    /// (<c>docs/ui/04-custom-button-null-background.md</c>).
    /// <para>Capture/apply rather than one generic call, so every <c>AddComponent</c> uses a
    /// concrete type under IL2CPP.</para>
    /// </summary>
    internal readonly struct ButtonStyle
    {
        /// <summary>False when there was no <c>MyButtonNormal</c>; apply is then a no-op.</summary>
        internal bool Captured { get; }

        private readonly MaskableGraphic background;
        private readonly Color defaultColor;
        private readonly Color hoverColor;
        private readonly Transform scaleOnHover;
        private readonly float hoverScale;
        private readonly Button button;
        private readonly GameObject disabledOverlay;
        private readonly AudioClip customSfx;

        private ButtonStyle(MyButtonNormal source)
        {
            Captured = true;

            background = source.background;
            defaultColor = source.defaultColor;
            hoverColor = source.hoverColor;
            scaleOnHover = source.scaleOnHover;
            hoverScale = source.hoverScale;
            button = source.button;
            disabledOverlay = source.disabledOverlay;
            customSfx = source.customSfx;
        }

        /// <summary>
        /// DestroyImmediate: a deferred Destroy would leave a dead <c>MyButton</c> for
        /// <c>FindAllButtonsInWindow</c> to collect.
        /// </summary>
        internal static ButtonStyle CaptureAndRemove(GameObject buttonObj)
        {
            if (buttonObj == null)
            {
                return default;
            }

            var original = buttonObj.GetComponent<MyButtonNormal>();
            if (original == null)
            {
                return default;
            }

            var style = new ButtonStyle(original);
            Object.DestroyImmediate(original);

            return style;
        }

        internal readonly void ApplyTo(MyButtonNormal replacement)
        {
            if (!Captured || replacement == null)
            {
                return;
            }

            replacement.background = background;
            replacement.defaultColor = defaultColor;
            replacement.hoverColor = hoverColor;
            replacement.scaleOnHover = scaleOnHover;
            replacement.hoverScale = hoverScale;
            replacement.button = button;
            replacement.disabledOverlay = disabledOverlay;
            replacement.customSfx = customSfx;
        }
    }
}
