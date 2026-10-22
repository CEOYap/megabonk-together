using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.ConstantAttacks
{
    [HarmonyPatch(typeof(CombatAura))]
    internal static class CombatAuraPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();


        /// <summary>Prevent non-local players to use Combat Aura</summary>

        [HarmonyPrefix]
        [HarmonyPatch(nameof(CombatAura.FixedUpdate))]
        public static bool FixedUpdate_Prefix(CombatAura __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // OB-20: dead players' objects stay alive in netplay, so check the aura OWNER's death,
            // not the local player's (the host ticks every peer's aura).
            if (IsOwnerDead(__instance))
            {
                return false;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;

            // OB-14: attribute the tick to the aura's owner; the finalizer clears it.
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

        /// <summary>No <c>NetPlayer</c> match means the local player.</summary>
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

                // Unknown peer is not dead; suppressing on a miss would delete living damage.
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
