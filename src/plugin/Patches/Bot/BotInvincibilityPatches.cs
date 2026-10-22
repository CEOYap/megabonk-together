// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using Assets.Scripts.Inventory__Items__Pickups;
using HarmonyLib;
using OmniBonk.Configuration;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Bot
{
    /// <summary>
    /// Test bot: the bot's own player cannot take damage. Decompiled: gates both <c>CanTakeDamage</c>
    /// (VA 0x180427960) and <c>CheckStopDamage</c> (VA 0x180427C20), covering every path into
    /// <c>Damage</c>. Local player only; teammates' avatars are untouched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerHealth))]
    internal static class BotInvincibilityPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>Installed only with BotMode on (trampoline cost). Needs a restart.</summary>
        [HarmonyPrepare]
        public static bool Prepare() => ModConfig.BotMode.Value && ModConfig.BotInvincible.Value;

        private static bool IsBotsOwnHealth(PlayerHealth health)
        {
            if (!ModConfig.BotMode.Value || !ModConfig.BotInvincible.Value || health == null || !synchronizationService.HasNetplaySessionStarted())
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
#endif
