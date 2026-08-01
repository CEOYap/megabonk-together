using MegabonkTogether.Configuration;
using UnityEngine;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// Swallows the first choice a reward window receives, for a short moment after it opens.
    ///
    /// <para><b>Why.</b> On a controller the same physical button is jump and confirm — "A" on
    /// Xbox, "✕" on PlayStation. Rewired binds them as two different actions (`Jump` and
    /// `UISubmit` are separate fields on <c>MyInputManager</c>), but the player only has one
    /// thumb. In shared experience the reward window opens because *somebody else* interacted
    /// with a chest or a shrine, so it can appear while you are mid-jump, and the press you made
    /// to jump lands on the window as a confirm. The choice is irreversible.</para>
    ///
    /// <para><b>Where the guard lives.</b> On the choice-commit methods, not on the input layer.
    /// Every reward window derives from <c>BaseEncounterWindow</c> and commits through
    /// <c>ChooseOffer(int)</c>, so guarding that (plus the chest's take/banish/discard buttons)
    /// covers mouse, keyboard and controller identically, and cannot interfere with navigation,
    /// camera or movement the way an input-layer filter would.</para>
    ///
    /// <para><b>It cannot strand a player.</b> The guard is purely a time window read from
    /// <c>Time.unscaledTime</c> — the game is paused while a window is up, so scaled time does not
    /// advance — and nothing has to happen for it to expire. Set the config entry to 0 to disable.</para>
    ///
    /// <para><b>Possible refinement, not done — needs one fact.</b> The stronger version of this
    /// is "require a fresh press": if the confirm action is already held when the window opens,
    /// ignore it until it is released and pressed again. That needs
    /// <c>MyInputManager.GetButton(MyInputManager.UISubmit)</c>, and the type of the
    /// <c>UISubmit</c> field is <b>UNVERIFIED</b> — the interop stubs carry the methods as
    /// <c>GetButton(String)</c>, so it is very likely a string action name, but a body is needed
    /// to confirm. If it is, this becomes a few lines on top and covers a held button of any
    /// duration. Until then the time window covers the common case: a tap that overlaps the
    /// window opening.</para>
    /// </summary>
    internal static class EncounterInputGrace
    {
        private static float armedAt = -999f;

        /// <summary>Called when a reward window opens.</summary>
        internal static void Arm()
        {
            armedAt = Time.unscaledTime;
        }

        /// <summary>True while a choice should be ignored as an accidental carry-over press.</summary>
        internal static bool IsBlocking()
        {
            var grace = ModConfig.EncounterInputGraceSeconds?.Value ?? 0f;
            if (grace <= 0f)
            {
                return false;
            }

            return Time.unscaledTime - armedAt < grace;
        }

        /// <summary>Clears the window, so a session teardown cannot leave one pending.</summary>
        internal static void Reset()
        {
            armedAt = -999f;
        }
    }
}
