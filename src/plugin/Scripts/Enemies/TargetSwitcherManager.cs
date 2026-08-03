using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Scripts.Enemies
{
    /// <summary>
    /// 04-performance-and-gc.md item 1A. <see cref="TargetSwitcher"/> is added to every enemy on
    /// the host, so at a full swarm the game was making ~600 managed <c>Update()</c> calls per
    /// frame, each crossing the IL2CPP managed↔native boundary — a crossing that is substantially
    /// more expensive for an Il2CppInterop-injected MonoBehaviour than a native Unity
    /// <c>Update</c>. Almost all of those calls did nothing but add to a timer and return.
    ///
    /// <para>This ticks every registered switcher from a single <c>Update</c>, so the interop
    /// crossing happens once per frame instead of once per enemy. The per-switcher work is now an
    /// ordinary C# method call.</para>
    ///
    /// <para><b>Deviation from the sketch in the doc:</b> that proposed slicing the list across
    /// frames (1/4 per frame with a compensated delta). Not done. The cost being removed is the
    /// interop crossing, not the arithmetic — <see cref="TargetSwitcher.Tick"/> is an add and a
    /// compare for all but a handful of entries per frame, and switch delays are randomised
    /// between 2-6s so firings already spread themselves out. Slicing would add cursor
    /// bookkeeping and an approximated delta for no measured gain. If profiling later shows the
    /// tick loop itself matters, slice then.</para>
    ///
    /// <para><b>Unmeasured.</b> No profiler capture has been taken before or after; the reasoning
    /// is structural.</para>
    /// </summary>
    public class TargetSwitcherManager : MonoBehaviour
    {
        /// <summary>Sized past the 600-enemy swarm so the list does not grow mid-run.</summary>
        private static readonly List<TargetSwitcher> switchers = new(700);

        /// <summary>
        /// Registration is idempotent — a switcher already in the list is ignored. That matters
        /// because enemies appear to be pooled, so the same component can be re-enabled many times.
        /// </summary>
        internal static void Register(TargetSwitcher switcher)
        {
            if (switcher == null || switcher.RegistryIndex >= 0)
            {
                return;
            }

            switcher.RegistryIndex = switchers.Count;
            switchers.Add(switcher);
        }

        /// <summary>
        /// Re-picks a target for every enemy currently chasing <paramref name="connectionId"/>,
        /// and returns how many were moved.
        ///
        /// <para><b>Why this exists (Run A).</b> <see cref="TargetSwitcher.Tick"/> assigns
        /// <c>enemy.target = currentTarget.rigidBody</c> — the departing player's <c>NetPlayer</c>
        /// Rigidbody. Nothing cleared it when that player disconnected, so the game's own enemy AI
        /// went on reading <c>.transform</c> off a destroyed object every frame. Run A measured
        /// ~700 dangling <c>get_transform</c> hits per 5s window on the host, sustained for the
        /// rest of the run and starting at the exact moment of the disconnect, with no
        /// MegabonkTogether frames in the sampled stack — because the reads are the game's, not
        /// ours. The P2-1 fallback in <c>Patches/Unity/UnityComponent.cs</c> was silently
        /// absorbing all of them.</para>
        ///
        /// <para>Host-only in practice: only the host runs enemy targeting, which is why only the
        /// host's log showed the fallbacks.</para>
        ///
        /// <para>Linear over the registry, but this runs once per disconnect, not per frame.</para>
        /// </summary>
        internal static int RetargetAllTargeting(uint connectionId)
        {
            var moved = 0;

            // Reverse, because a switcher whose enemy was destroyed unregisters itself during the
            // loop and Unregister swap-removes.
            for (var i = switchers.Count - 1; i >= 0; i--)
            {
                if (i >= switchers.Count)
                {
                    continue;
                }

                var switcher = switchers[i];
                if (switcher == null || switcher.CurrentTargetNetplayId != connectionId)
                {
                    continue;
                }

                if (switcher.RetargetAwayFromDepartedPlayer())
                {
                    moved++;
                }
            }

            return moved;
        }

        /// <summary>
        /// O(1) swap-remove. A linear <c>List.Remove</c> would be O(n) per despawn, and at a swarm
        /// despawns are frequent enough for that to matter.
        /// </summary>
        internal static void Unregister(TargetSwitcher switcher)
        {
            if (switcher == null)
            {
                return;
            }

            var index = switcher.RegistryIndex;
            switcher.RegistryIndex = -1;

            // Guard against a stale index: the registry is cleared wholesale between sessions.
            if (index < 0 || index >= switchers.Count || switchers[index] != switcher)
            {
                return;
            }

            RemoveAt(index);
        }

        /// <summary>
        /// Swap-removes by position. Separate from <see cref="Unregister"/> because a destroyed
        /// switcher compares equal to null through Unity's operator, so Unregister's null guard
        /// would reject it and leave the dead entry in the list forever.
        /// </summary>
        private static void RemoveAt(int index)
        {
            var last = switchers.Count - 1;
            if (index != last)
            {
                switchers[index] = switchers[last];
                if (switchers[index] != null)
                {
                    switchers[index].RegistryIndex = index;
                }
            }

            switchers.RemoveAt(last);
        }

        /// <summary>Drops every registration. Called on teardown so a new session starts clean.</summary>
        internal static void Clear()
        {
            foreach (var switcher in switchers)
            {
                if (switcher != null)
                {
                    switcher.RegistryIndex = -1;
                }
            }

            switchers.Clear();
        }

        public void Update()
        {
            if (switchers.Count == 0)
            {
                return; // singleplayer, or no enemies yet — one no-op call per frame
            }

            var deltaTime = Time.deltaTime;

            // Iterate backwards: if a Tick ever causes an unregister, swap-remove moves a later
            // element into the freed slot, and going backwards means that slot is already done.
            for (var i = switchers.Count - 1; i >= 0; i--)
            {
                if (i >= switchers.Count)
                {
                    continue; // the list shrank underneath us
                }

                var switcher = switchers[i];
                if (switcher == null)
                {
                    // Destroyed without OnDisable firing. Remove by position — see RemoveAt.
                    RemoveAt(i);
                    continue;
                }

                switcher.Tick(deltaTime);
            }
        }
    }
}
