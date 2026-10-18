using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using MegabonkTogether.Configuration;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Bot
{
    /// <summary>
    /// Test bot: the bot's own player cannot take damage (design goal 3, the owner's choice).
    ///
    /// <para><b>Two gates, decompiled.</b> <c>DamagePlayer</c> (VA <c>0x180428180</c>) and
    /// <c>DamagePlayerExternal</c> (VA <c>0x180427FD0</c>), the two public ways in, both check
    /// <c>CanTakeDamage</c> (VA <c>0x180427960</c>) before calling the private <c>Damage</c>
    /// (VA <c>0x180428220</c>), which in turn bails when <c>CheckStopDamage</c>
    /// (VA <c>0x180427C20</c>) returns true. Gating both covers any caller that reaches
    /// <c>Damage</c> directly.</para>
    ///
    /// <para>Only the local player's health, on a copy with <c>BotMode</c> on. Teammates' avatars
    /// hold their own <c>PlayerHealth</c> and are left alone; HP is owner-authoritative, so the
    /// host just sees a teammate at full health.</para>
    /// </summary>
    [HarmonyPatch(typeof(PlayerHealth))]
    internal static class BotInvincibilityPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// Installed only on a BotMode copy: otherwise every player's copy paid a trampoline and a
        /// PlayerHealth wrapper on every damage check. Turning BotMode on needs a restart.
        /// </summary>
        [HarmonyPrepare]
        public static bool Prepare() => ModConfig.BotMode.Value;

        private static bool IsBotsOwnHealth(PlayerHealth health)
        {
            if (!ModConfig.BotMode.Value || health == null || !synchronizationService.HasNetplaySessionStarted())
            {
                return false;
            }

            var local = GameManager.Instance?.player?.inventory?.playerHealth;
            return local != null && local.Pointer == health.Pointer;
        }

        // Prefix-false on purpose: the bot's own player is invincible.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerHealth.CanTakeDamage))]
        public static bool CanTakeDamage_Prefix(PlayerHealth __instance, ref bool __result)
        {
            if (!IsBotsOwnHealth(__instance))
            {
                return true;
            }

            __result = false;
            return false;
        }

        // Prefix-false on purpose: "stop this damage" for the bot's own player.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(PlayerHealth.CheckStopDamage))]
        public static bool CheckStopDamage_Prefix(PlayerHealth __instance, ref bool __result)
        {
            if (!IsBotsOwnHealth(__instance))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }
}
