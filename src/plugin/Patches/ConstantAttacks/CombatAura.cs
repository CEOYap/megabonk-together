using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.ConstantAttacks
{
    [HarmonyPatch(typeof(CombatAura))]
    internal static class CombatAuraPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();


        /// <summary>
        /// Prevent non-local players to use Combat Aura
        /// </summary>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(CombatAura.FixedUpdate))]
        public static bool FixedUpdate_Prefix(CombatAura __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // OB-20. A player who is dead keeps damaging things with their aura.
            //
            // Death is prevented rather than processed in a netplay session -- PreventDeath swaps
            // the game's A_Died handler for one that only moves the camera -- so a dead player's
            // object stays alive and every ConstantAttack hanging off it keeps ticking. The host
            // runs FixedUpdate for ALL auras, its own and every peer's, which is why this cannot
            // just ask whether the local player is dead: that would stop a living client's aura
            // whenever the host died.
            //
            // Ownership comes from ConstantAttack.weaponBase through the same resolver
            // WeaponUtility already uses for OB-1, so there is no second notion of "whose attack
            // is this" to keep in step.
            if (IsOwnerDead(__instance))
            {
                return false;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            // OB-14. The host runs every player's aura, so a teammate's aura damage was applied
            // with no attribution and RunStats filed it under the host. Tell the tracker whose aura
            // this is for the duration of the tick, exactly as ProjectileBase.HitEnemy does for a
            // teammate's projectiles; the finalizer clears it.
            if (isHost && __instance.weaponBase != null)
            {
                // OB-41: the inventory store, which survives a level change.
                var owner = playerManagerService.GetWeaponOwnerId(__instance.weaponBase);
                if (owner.HasValue)
                {
                    trackerService.SetCurrentPlayerId(owner.Value);
                    __state = true;
                }
            }

            return isHost;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(CombatAura.FixedUpdate))]
        public static void FixedUpdate_Finalizer(bool __state)
        {
            if (__state)
            {
                trackerService.UnsetCurrentPlayerId();
            }
        }

        /// <summary>
        /// True when the player this aura belongs to is dead. A <c>NetPlayer</c> match means a
        /// remote peer, answered from the replicated model; no match means the local player.
        /// </summary>
        private static bool IsOwnerDead(CombatAura aura)
        {
            var weaponBase = aura == null ? null : aura.weaponBase;
            if (weaponBase == null)
            {
                return false;
            }

            var netPlayer = playerManagerService.GetNetPlayerByWeapon(weaponBase);
            if (netPlayer != null)
            {
                var owner = playerManagerService.GetPlayer(netPlayer.ConnectionId);

                // A peer we have no record for is not treated as dead: suppressing on a lookup
                // miss would silently delete a living player's damage, which is the worse error.
                return owner != null && owner.Hp == 0;
            }

            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.player == null)
            {
                return false;
            }

            return gameManager.player.IsDead();
        }
    }
}
