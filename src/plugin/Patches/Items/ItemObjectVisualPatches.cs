using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using HarmonyLib;
using OmniBonk.Helpers;
using UnityEngine;

namespace OmniBonk.Patches.Items
{
    /// <summary>
    /// OB-62. Dragonfire (<c>ProcOnHitEffects</c> 0x180454C90) takes <c>firefieldPool</c> (0x1C8) and calls
    /// <c>Firefield.Set</c> with no weapon. The field damages in <c>CheckDamage</c> and <c>TryPopDamage</c>;
    /// a replayed copy is set with zero damage and both are skipped for it.
    /// </summary>
    [HarmonyPatch(typeof(Firefield))]
    internal static class FirefieldVisualPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Firefield.Set))]
        private static void Set_Postfix(Firefield __instance, float radius, float duration, WeaponBase weaponBase)
        {
            if (weaponBase == null && !ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureFirefield(__instance, radius, duration);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Firefield.CheckDamage))]
        private static bool CheckDamage_Prefix(Firefield __instance) => !ItemVisualSync.IsReplayedObject(__instance.gameObject);

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Firefield.TryPopDamage))]
        private static bool TryPopDamage_Prefix(Firefield __instance) => !ItemVisualSync.IsReplayedObject(__instance.gameObject);
    }

    /// <summary>
    /// OB-62. Borgor (<c>OnEnemyDied</c> 0x18043BCE0) is host-run, so its burgers existed only on the host,
    /// where only the host could eat them. Decompiled: <c>Borgar.OnCollisionEnter</c> (0x18048AC10) heals
    /// <c>MyPlayer</c> when a player-layer object touches it, then calls <c>DestroyBorgar</c>
    /// (0x18048AB30). Copies on clients heal whoever eats them there; an eaten burger is removed
    /// everywhere else.
    /// </summary>
    [HarmonyPatch(typeof(Borgar))]
    internal static class BorgorPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Borgar.Set))]
        private static void Set_Postfix(Borgar __instance, int flatHeal, float ratioHeal)
        {
            if (!ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureBorgorSpawn(__instance, flatHeal, ratioHeal);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("OnCollisionEnter")]
        private static void OnCollisionEnter_Prefix(Borgar __instance, out bool __state) => __state = __instance.gameObject.activeSelf;

        [HarmonyPostfix]
        [HarmonyPatch("OnCollisionEnter")]
        private static void OnCollisionEnter_Postfix(Borgar __instance, bool __state)
        {
            if (__state && !__instance.gameObject.activeSelf && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureBorgorEaten(__instance);
                ItemVisualSync.ForgetBorgor(__instance);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch("DestroyBorgar")]
        private static void DestroyBorgar_Prefix(Borgar __instance)
        {
            if (!ItemVisualSync.IsReplaying)
            {
                ItemVisualSync.ForgetBorgor(__instance);
            }
        }
    }

    /// <summary>OB-62. Campfire (<c>CreateCamp</c> 0x18043D380 / <c>RemoveCamp</c> 0x18043D770) is owner-run; the camp is visual on others.</summary>
    [HarmonyPatch(typeof(Campfire))]
    internal static class CampfireVisualPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Campfire.StartFire))]
        private static void StartFire_Prefix(Vector3 pos)
        {
            if (!ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureCampStart(pos);
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(Campfire.EndFire))]
        private static void EndFire_Prefix()
        {
            if (!ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureCampEnd();
            }
        }
    }

    /// <summary>
    /// OB-62. Bob's Lantern <c>Explode</c> (0x18043A990) instantiates EM <c>lanternExplosion</c> once
    /// (a <c>KillEnemyTrigger</c>), then moves it to <c>MyPlayer</c> and re-activates it each time.
    /// </summary>
    [HarmonyPatch(typeof(ItemBobLantern), nameof(ItemBobLantern.Explode))]
    internal static class BobLanternVisualPatches
    {
        private static void Postfix(ItemBobLantern __instance)
        {
            if (!ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureLantern(__instance.explosionPrefab);
            }
        }
    }
}
