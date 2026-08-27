using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Interactables
{
    [HarmonyPatch(typeof(InteractableShrineChallenge))]
    internal static class InteractableShrineChallengePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// OB-15. The client must not run challenge bookkeeping — it has none to run, and running it
        /// threw <b>13,878</b> times in one session.
        ///
        /// <para><b>The chain, and the mod is what closes it.</b> <c>EnemyDied</c> is invoked from
        /// <c>Enemy.ReleaseToPool</c>, so it fires for every enemy the pool reclaims — which is why
        /// the storm tracked enemy deaths and peaked during a swarm. Decompiled
        /// (<c>InteractableShrineChallenge$$EnemyDied</c>, VA <c>0x1804CDFE0</c>) the body opens by
        /// returning early unless <c>done</c> is set, then walks <c>enemies</c> — a
        /// <c>HashSet&lt;Enemy&gt;</c> at <c>0x78</c> — and dereferences <c>EffectManager.Instance</c>
        /// and the enemy on the completion path.</para>
        ///
        /// <para><b>On a client that set is never populated.</b>
        /// <c>SynchronizationService.OnReceivedInteractableUsed</c> deliberately does not call
        /// <c>Interact()</c> on a client; it applies the cosmetic result instead —
        /// <c>done = true</c>, <c>fx</c> on, <c>alertIcon</c> destroyed. That shortcut sets the very
        /// flag <c>EnemyDied</c> gates on, so the mod converts a handler that would have returned
        /// immediately into one that walks state nobody built.</para>
        ///
        /// <para><b>Why suppress rather than null-guard.</b> A null check would silence the
        /// exception and leave a client-side challenge that can never complete — the symptom gone,
        /// the lie intact. Challenges are host-authoritative: the host runs <c>Interact()</c>, owns
        /// the enemy set, and its completion reaches the client as replicated spawns and the
        /// cosmetic shortcut above. There is nothing here for a client to compute.</para>
        ///
        /// <para><b>Same shape as <see cref="Items.ItemGhostPatches"/> (OB-2)</b>: a global game
        /// event invoking an <i>instance</i> handler on an object whose state this peer never
        /// initialised. That is now twice, so it is a pattern rather than a coincidence — worth
        /// checking for wherever the mod fakes a result locally instead of running the real path.</para>
        ///
        /// <para><b>UNVERIFIED.</b> Not run in-game. What confirms it is the client's
        /// <c>Player.log</c> going from 13,878 <c>NullReferenceException</c> lines to none, with the
        /// challenge shrine still showing as completed on that peer.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(InteractableShrineChallenge.EnemyDied))]
        public static bool EnemyDied_Prefix()
        {
            // Initialized, NOT Started. The first version used HasNetplaySessionStarted and left
            // 394 exceptions behind on 2026-08-18, all of them during a level transition: the
            // session state leaves Started while the world is being torn down and rebuilt, so the
            // guard opened at exactly the moment enemies were being released to the pool. A match
            // having been found stays true across a transition, which is the window that matters.
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            // Host owns the challenge; clients receive its results.
            return synchronizationService.IsServerMode() ?? false;
        }
    }
}
