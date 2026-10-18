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
        private static string interact;

        /// <summary>
        /// Installed only on a BotMode copy. Without this every player's copy paid a
        /// native-to-managed trampoline, with a managed string copy, on every GetAxis and
        /// GetButtonDown call, and a bot patch that failed to resolve would abort PatchAll for
        /// everyone. Turning BotMode on therefore needs a restart.
        /// </summary>
        [HarmonyPrepare]
        public static bool Prepare() => ModConfig.BotMode.Value;

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

        /// <summary>
        /// Decompiled: <c>PlayerInput.MovementInput</c> reads interact as
        /// <c>GetButtonDown(MyInputManager.Interact)</c> once a frame, so one latched press from
        /// <see cref="BotInput.PressInteract"/> lands as exactly one interact. The auto-updater prefix
        /// on the same method (<c>Patches/MyInputManager.cs</c>) still runs; it only blocks input
        /// while an update is pending.
        /// </summary>
        // Prefix-false on purpose: the bot presses interact itself.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MyInputManager.GetButtonDown))]
        public static bool GetButtonDown_Prefix(string action, ref bool __result)
        {
            if (!IsSteering())
            {
                return true;
            }

            interact ??= MyInputManager.Interact;
            if (action != interact || !BotInput.ConsumeInteract(BotController.Instance.CurrentInteractableId()))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }
}
