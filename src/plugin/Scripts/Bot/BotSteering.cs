// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using UnityEngine;

namespace OmniBonk.Scripts.Bot
{
    /// <summary>
    /// Pure steering maths for the test bot: no game state, only vectors in and out.
    /// </summary>
    internal static class BotSteering
    {
        /// <summary>Seconds for one lap of the ring around the host.</summary>
        private const float LAP_SECONDS = 20f;

        /// <summary>A point circling the host, so the bot strafes rather than parks.</summary>
        internal static Vector3 PickFollowPoint(Vector3 owner, Vector3 bot, float followDistance, float time)
        {
            var angle = time / LAP_SECONDS * 2f * Mathf.PI;
            var radius = 0.6f * followDistance;
            return owner + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
        }

        /// <summary>
        /// Stick input for <paramref name="worldDirection"/>, projected onto <paramref name="feet"/>'s
        /// forward/right (decompiled <c>InputVectorRelativeToLook</c>, VA 0x18033E040).
        /// </summary>
        internal static Vector2 ToInputAxes(Vector3 worldDirection, Transform feet)
        {
            var forward = feet.forward;
            forward.y = 0f;
            var right = feet.right;
            right.y = 0f;
            worldDirection.y = 0f;

            if (forward.sqrMagnitude < 1e-6f || right.sqrMagnitude < 1e-6f || worldDirection.sqrMagnitude < 1e-6f)
            {
                return Vector2.zero;
            }

            forward.Normalize();
            right.Normalize();
            var direction = worldDirection.normalized;
            return Vector2.ClampMagnitude(new Vector2(Vector3.Dot(direction, right), Vector3.Dot(direction, forward)), 1f);
        }
    }
}
#endif
