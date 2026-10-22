using Assets.Scripts.UI.InGame.Levelup;
using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using OmniBonk.Helpers;

namespace OmniBonk.Patches
{
    /// <summary>
    /// Arms <see cref="EncounterInputGrace"/> when a reward window opens and drops choices made
    /// inside the grace period.
    /// <para>Not gated on <c>HasNetplaySessionStarted()</c> on purpose: confirm-is-also-jump
    /// misfires in singleplayer too. The config entry is the off switch.</para>
    /// <para><b>CONFIRMED in-game: never patch the virtual <c>Open</c>/<c>ChooseOffer</c>
    /// overrides.</b> Under Il2CppInterop, calling the original of a patched virtual re-dispatches
    /// through the replaced vtable slot, recursing to a stack overflow inside <c>PatchAll</c>
    /// (f378248, reverted by e3eafde). Only non-virtual methods are patched here.</para>
    /// </summary>
    [HarmonyPatch(typeof(LevelupScreen))]
    internal static class LevelupScreenChoiceGuardPatches
    {
        /// <summary>
        /// Non-virtual, so patchable. <b>UNVERIFIED:</b> that it runs on every level-up window when
        /// it becomes interactive. Fails toward an inert guard.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(LevelupScreen.ShowLevelupScreen))]
        public static void ShowLevelupScreen_Postfix() => EncounterInputGrace.Arm();

        // ChooseOffer is virtual and cannot be patched; the item pick is guarded via UpgradeButton below.

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LevelupScreen.Banish))]
        public static bool Banish_Prefix() => !EncounterInputGrace.IsBlocking();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LevelupScreen.Skip))]
        public static bool Skip_Prefix() => !EncounterInputGrace.IsBlocking();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LevelupScreen.Leave))]
        public static bool Leave_Prefix() => !EncounterInputGrace.IsBlocking();
    }

    /// <summary>
    /// Guards the level-up item pick via the non-virtual <c>SelectUpgrade</c> (<c>OnClick</c> is
    /// virtual, same trap as <c>ChooseOffer</c>).
    /// <para><b>UNVERIFIED:</b> that every offer click goes through <c>SelectUpgrade</c>, and that
    /// moai, shady guy and balance shrine reuse <c>UpgradeButton</c>.</para>
    /// </summary>
    [HarmonyPatch(typeof(UpgradeButton))]
    internal static class UpgradeButtonChoiceGuardPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(UpgradeButton.SelectUpgrade))]
        public static bool SelectUpgrade_Prefix() => !EncounterInputGrace.IsBlocking();
    }

    [HarmonyPatch(typeof(ChestWindowUi))]
    internal static class ChestWindowChoiceGuardPatches
    {
        /// <summary>
        /// Non-virtual; fires when the offer becomes actionable. <b>UNVERIFIED</b>, as
        /// <c>ShowLevelupScreen_Postfix</c>.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestWindowUi.OpeningFinished))]
        public static void OpeningFinished_Postfix() => EncounterInputGrace.Arm();

        // ChooseOffer is virtual; the chest's real commits are the buttons below.

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.TakeButton))]
        public static bool TakeButton_Prefix() => !EncounterInputGrace.IsBlocking();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.BanishButton))]
        public static bool BanishButton_Prefix() => !EncounterInputGrace.IsBlocking();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.DiscardButton))]
        public static bool DiscardButton_Prefix() => !EncounterInputGrace.IsBlocking();

        // OpenButton is not guarded: it commits nothing.
    }
}
