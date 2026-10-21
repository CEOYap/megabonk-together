using Assets.Scripts.Game.MapGeneration;
using HarmonyLib;
using MegabonkTogether.Helpers;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using MonoMod.Utils;

namespace MegabonkTogether.Patches
{
    [HarmonyPatch(typeof(RandomObjectPlacer))]
    internal class RandomObjectPlacerPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Skip object spawning on clients
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(RandomObjectPlacer.RandomObjectSpawner))]
        public static bool RandomObjectSpawner_Prefix(RandomObjectPlacer __instance)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return true;
            }

            var isServer = synchronizationService.IsServerMode() ?? false;
            if (!isServer)
            {
                return false;
            }

            return true;

        }

        /// <summary>
        /// Send spawned objects to clients
        /// </summary>

        [HarmonyPostfix]
        [HarmonyPatch(nameof(RandomObjectPlacer.RandomObjectSpawner))]
        public static void RandomObjectSpawner_Postfix(RandomObjectPlacer __instance, RandomMapObject randomObject)
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            var isServer = synchronizationService.IsServerMode();
            if (isServer.HasValue && !isServer.Value)
            {
                return;
            }

            foreach (var prefab in randomObject.prefabs)
            {
                var inGame = Il2CppFindHelper.FindAllGameObjects();
                foreach (var obj in inGame)
                {
                    if (IsCopyOf(obj.name, prefab.name) && obj.name != "BarrelMesh" && obj.name != "ArchCollider")
                    {
                        if (prefab.name.Contains("Microwave"))
                        {
                            continue; //Skip microwaves , handled in MicrowavePatches
                        }

                        var dynamic = DynamicData.For(obj);
                        var hasBeenSet = dynamic.Get<bool?>("hasBeenSetByServer");
                        if (!hasBeenSet.HasValue)
                        {
                            synchronizationService.OnSpawnedObject(obj);

                            dynamic.Set("hasBeenSetByServer", true);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A spawned copy of <paramref name="prefab"/>: its own name, or Unity's "Name(Clone)" and
        /// "Name (n)". A bare prefix also matched other objects whose names merely start the same:
        /// <c>ChargeShrineFinishFx</c>, a charge shrine's finishing effect, was sent as a spawned
        /// <c>ChargeShrine</c>, and no client has a prefab by that name (2026-10-21: four per Ghost
        /// King room, each retried until given up).
        /// </summary>
        private static bool IsCopyOf(string name, string prefab)
            => name == prefab
               || (name.Length > prefab.Length
                   && name.StartsWith(prefab, System.StringComparison.Ordinal)
                   && (name[prefab.Length] == '(' || name[prefab.Length] == ' '));
    }
}
