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

        /// <summary>Latches one interact press for the next <see cref="ConsumeInteract"/>.</summary>
        internal static void PressInteract() => interactLatched = true;

        /// <summary>True exactly once after each <see cref="PressInteract"/>.</summary>
        internal static bool ConsumeInteract()
        {
            if (!interactLatched)
            {
                return false;
            }

            interactLatched = false;
            return true;
        }

        internal static void Clear()
        {
            Move = Vector2.zero;
            interactLatched = false;
        }
    }
}
