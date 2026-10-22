// Developer test tooling: compiled into Debug builds only, never into a release (the release
// workflows build Release). See docs/netplay/05-local-testing.md.
#if DEBUG
using HarmonyLib;
using OmniBonk.Configuration;
using OmniBonk.Scripts.Bot;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Bot
{
    /// <summary>
    /// Test bot: feeds <see cref="BotInput"/> through the controller input read, so it replicates like a player.
    /// Decompiled: <c>PlayerInput.MovementInput</c> is the only gameplay caller of <c>GetAxis</c> (VA 0x1804D4C30).
    /// </summary>
    [HarmonyPatch(typeof(MyInputManager))]
    internal static class BotInputPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        // Cached: reading an IL2CPP static string marshals, and GetAxis runs several times a frame.
        private static string moveHorizontal;
        private static string moveVertical;
        private static string interact;

        /// <summary>Installed only with BotMode on (trampoline cost; a failure would abort PatchAll). Needs a restart.</summary>
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

        /// <summary>Decompiled: interact is read once a frame, so one latched press is one interact.</summary>
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
#endif
