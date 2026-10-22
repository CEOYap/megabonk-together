using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using static Assets.Scripts.Game.Combat.EnemySpecialAttacks.Implementations.EnemySpecialAttackFollowing;

namespace OmniBonk.Patches.SpecialAttack
{
    [HarmonyPatch(typeof(_DoAttack_d__5))]
    internal static class EnemySpecialAttackFollowingPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Targets the enemy's actual target; the original always targets the local player.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(_DoAttack_d__5.MoveNext))]
        public static void MoveNext_Prefix(_DoAttack_d__5 __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var targetId = DynamicData.For(__instance.__4__this.enemy).Get<uint?>("targetId");
            if (targetId.HasValue)
            {
                playerManagerService.AddGetNetplayerPositionRequest(targetId.Value);
                __state = true;
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(_DoAttack_d__5.MoveNext))]
        public static void MoveNext_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}
