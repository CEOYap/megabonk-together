using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Scripts.Enemies
{
    /// <summary>
    /// PERF 1A. Ticks every <see cref="TargetSwitcher"/> from one <c>Update</c>, so the costly
    /// IL2CPP injected-<c>Update</c> crossing happens once per frame instead of once per enemy.
    /// <para><b>Unmeasured</b>; no profiler capture before or after.</para>
    /// </summary>
    public class TargetSwitcherManager : MonoBehaviour
    {
        /// <summary>Sized past the 600-enemy swarm so the list does not grow mid-run.</summary>
        private static readonly List<TargetSwitcher> switchers = new(700);

        /// <summary>Idempotent: pooled enemies re-enable the same component many times.</summary>
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
        /// Retargets every enemy chasing <paramref name="connectionId"/>; returns how many moved.
        /// Otherwise the game's AI keeps reading a departed player's destroyed Rigidbody (P2-1). Host only.
        /// </summary>
        internal static int RetargetAllTargeting(uint connectionId)
        {
            var moved = 0;

            // Reverse: switchers can swap-remove themselves during the loop.
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

        /// <summary>O(1) swap-remove; despawns are frequent at a swarm.</summary>
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

        /// <summary>By position: a destroyed switcher is Unity-null, so <see cref="Unregister"/> would reject it.</summary>
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

            // Backwards, so a swap-remove during Tick only moves already-ticked entries.
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
