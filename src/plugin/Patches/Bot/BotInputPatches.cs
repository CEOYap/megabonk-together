using HarmonyLib;
using MegabonkTogether.Configuration;
using MegabonkTogether.Scripts.Bot;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Bot
{
    /// <summary>
    /// Test bot: hands <see cref="BotInput"/> to the game through the same input read a controller
    /// goes through, so the bot moves like a player and its movement reaches the host as an ordinary
    /// <c>PlayerUpdate</c>.
    ///
    /// <para>Decompiled: <c>PlayerInput.MovementInput</c> (the only gameplay caller of
    /// <c>MyInputManager.GetAxis</c>, VA <c>0x1804D4C30</c>) reads the axes named by the static
    /// fields <c>MyInputManager.MoveHorizontal</c> (0x60) and <c>MoveVertical</c> (0x68). Only those
    /// two names are answered; every other axis runs the original.</para>
    /// </summary>
    [HarmonyPatch(typeof(MyInputManager))]
    internal static class BotInputPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        // Cached: reading an IL2CPP static string marshals, and GetAxis runs several times a frame.
        private static string moveHorizontal;
        private static string moveVertical;

        private static bool IsSteering()
        {
            if (!ModConfig.BotMode.Value || !synchronizationService.HasNetplaySessionStarted())
            {
                return false;
            }

            var bot = BotController.Instance;
            return bot != null
                && (bot.State == BotController.BotState.Following
                    || bot.State == BotController.BotState.Detouring
                    || bot.State == BotController.BotState.Reviving);
        }

        // Prefix-false on purpose: the bot answers the two movement axes itself.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MyInputManager.GetAxis))]
        public static bool GetAxis_Prefix(string action, ref float __result)
        {
            if (!IsSteering())
            {
                return true;
            }

            moveHorizontal ??= MyInputManager.MoveHorizontal;
            moveVertical ??= MyInputManager.MoveVertical;

            if (action == moveHorizontal)
            {
                __result = BotInput.Move.x;
                return false;
            }

            if (action == moveVertical)
            {
                __result = BotInput.Move.y;
                return false;
            }

            return true;
        }
    }
}
