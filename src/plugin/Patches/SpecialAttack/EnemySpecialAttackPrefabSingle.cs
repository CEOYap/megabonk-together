using Assets.Scripts.Game.Combat.EnemySpecialAttacks.Implementations;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace OmniBonk.Patches.SpecialAttack
{
    [HarmonyPatch(typeof(EnemySpecialAttackPrefabSingle))]
    internal static class EnemySpecialAttackPrefabSinglePatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>Targets the enemy's actual target; the original always targets the local player.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EnemySpecialAttackPrefabSingle.Init))]
        public static void Init_Prefix(EnemySpecialAttackPrefabSingle __instance, out bool __state)
        {
            __state = false;

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return;
            }

            var targetId = DynamicData.For(__instance.enemy).Get<uint?>("targetId");
            if (targetId.HasValue)
            {
                playerManagerService.AddGetNetplayerPositionRequest(targetId.Value);
                __state = true;
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EnemySpecialAttackPrefabSingle.Init))]
        public static void Init_Finalizer(bool __state)
        {
            if (!__state)
            {
                return;
            }

            playerManagerService.UnqueueNetplayerPositionRequest();
        }
    }
}
