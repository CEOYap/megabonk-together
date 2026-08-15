using UnityEngine;
using UnityEngine.UI;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// The serialized state of a game button, carried across the swap that replaces its
    /// <c>MyButtonNormal</c> with one of ours.
    ///
    /// <para><b>What this fixes.</b> Every mod-made button is a clone of one of the game's, with its
    /// <c>MyButtonNormal</c> destroyed and a bare <c>CustomButton</c> added in its place. Those two
    /// steps discard eight serialized fields, and one of them — <c>background</c> — is dereferenced
    /// by <c>MyButtonNormal.SetColor</c> on every hover. So every mod button threw a
    /// <c>NullReferenceException</c> each time the pointer touched it: 1716 of them on one peer in a
    /// single session, the mod's largest source of exceptions by two orders of magnitude. The rest
    /// of the fields are why mod buttons never had the game's hover scaling, colour states or
    /// greyed-out look. Full diagnosis: <c>docs/ui/04-custom-button-null-background.md</c>.</para>
    ///
    /// <para><b>Why a capture/apply pair rather than one call that does the swap.</b> The component
    /// being added differs per call site — <c>CustomButton</c> at five of them, <c>PlayTogetherButton</c>
    /// at the sixth — and a generic <c>AddComponent&lt;T&gt;</c> resolves its IL2CPP type at runtime
    /// from the type argument. Splitting it leaves every <c>AddComponent</c> written against a
    /// concrete type, which is the form the runtime is known to handle. <see cref="ApplyTo"/> takes
    /// the base <c>MyButtonNormal</c>, so both derived types go through the same path.</para>
    /// </summary>
    internal readonly struct ButtonStyle
    {
        /// <summary>False when the object had no <c>MyButtonNormal</c> to copy — apply is then a no-op.</summary>
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
        /// Reads the original's state and removes it, leaving the object ready for a replacement.
        ///
        /// <para><b>Immediate, not deferred.</b> <c>Destroy</c> runs at the end of the frame, which
        /// would leave two <c>MyButton</c>-derived components on one object for the rest of it — and
        /// <c>Window.FindAllButtonsInWindow</c> collects every <c>MyButton</c> it can see, so the
        /// registry would hold a corpse alongside the live button.</para>
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

        /// <summary>
        /// Writes the captured state onto the replacement. Safe to call with nothing captured.
        /// </summary>
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
