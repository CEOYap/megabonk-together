// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using UnityEngine;

namespace MegabonkTogether.Scripts.Bot
{
    /// <summary>
    /// What the test bot is pressing this frame. <see cref="BotController"/> writes it; the input
    /// patches in <c>Patches/Bot/BotInputPatches.cs</c> hand it to the game as if a controller had
    /// produced it, so the bot moves and interacts through the same reads a player's input goes
    /// through. Main thread only.
    /// </summary>
    internal static class BotInput
    {
        /// <summary>Movement stick, x = right, y = forward, length at most 1.</summary>
        internal static Vector2 Move;

        private static bool interactLatched;
        private static int interactTargetId;

        /// <summary>
        /// Latches one interact press meant for the interactable with <paramref name="targetId"/>
        /// (<c>GetInstanceID</c>). The press only lands while that object is still the game's current
        /// interactable, so a press left over from a detour can never use a portal or boss spawner
        /// the bot drifted next to afterwards.
        /// </summary>
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
