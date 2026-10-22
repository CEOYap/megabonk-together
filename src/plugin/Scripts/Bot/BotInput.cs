// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using UnityEngine;

namespace OmniBonk.Scripts.Bot
{
    /// <summary>The test bot's input this frame, fed to the game by <c>BotInputPatches</c>. Main thread only.</summary>
    internal static class BotInput
    {
        /// <summary>Movement stick, x = right, y = forward, length at most 1.</summary>
        internal static Vector2 Move;

        private static bool interactLatched;
        private static int interactTargetId;

        /// <summary>Latches one press, landing only while <paramref name="targetId"/> is still the current interactable.</summary>
        internal static void PressInteract(int targetId)
        {
            interactLatched = true;
            interactTargetId = targetId;
        }

        /// <summary>True exactly once after a press, and only while its target is <paramref name="currentId"/>.</summary>
        internal static bool ConsumeInteract(int? currentId)
        {
            if (!interactLatched || currentId != interactTargetId)
            {
                return false;
            }

            interactLatched = false;
            return true;
        }

        /// <summary>Drops a press that has not landed yet.</summary>
        internal static void CancelInteract() => interactLatched = false;

        internal static void Clear()
        {
            Move = Vector2.zero;
            interactLatched = false;
        }
    }
}
#endif
