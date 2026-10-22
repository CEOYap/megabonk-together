using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;
using static EnemySpecialAttackManyRandom;

namespace OmniBonk.Patches.SpecialAttack
{
    [HarmonyPatch(typeof(_DoAttack_d__8))]
    internal static class EnemySpecialAttackManyRandomPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Aim at the enemy's actual target; the original always targets the local player.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(_DoAttack_d__8.MoveNext))]
        public static void MoveNext_Prefix(_DoAttack_d__8 __instance, out bool __state)
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
        [HarmonyPatch(nameof(_DoAttack_d__8.MoveNext))]
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
