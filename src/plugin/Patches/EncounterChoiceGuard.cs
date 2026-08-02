using Assets.Scripts.UI.InGame.Levelup;
using Assets.Scripts.UI.InGame.Rewards;
using HarmonyLib;
using MegabonkTogether.Helpers;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// Arms <see cref="EncounterInputGrace"/> when a reward window opens, and drops the choices
    /// that arrive inside that window.
    ///
    /// <para>Both concrete reward windows derive from <c>BaseEncounterWindow</c> and share its
    /// <c>Open(EEncounter)</c> / <c>ChooseOffer(int)</c> shape — <c>LevelupScreen</c> also backs the
    /// moai, shady guy and balance shrine offers (see its <c>GetMoaiText</c> / <c>GetShadyGuyText</c>),
    /// so these two types cover every offer surface in the game. The abstract base cannot be
    /// patched; the overrides can.</para>
    ///
    /// <para>Unlike every other patch in this repo these are <b>not</b> gated on
    /// <c>HasNetplaySessionStarted()</c>. The confirm-is-also-jump collision is a property of the
    /// controller, not of netplay — netplay only makes it far more likely by opening windows the
    /// player did not trigger. Gating it would mean the mod fixes the annoying case and leaves the
    /// same misfire in singleplayer, which is a worse experience for no reason. The config entry is
    /// the off switch.</para>
    ///
    /// <para><b>The paragraph above is kept because its conclusion still stands, but its reasoning
    /// was incomplete and it cost a launch.</b> It treated <c>HasNetplaySessionStarted()</c> as
    /// purely a scope decision. It is also, accidentally, load-order protection: it is an enum
    /// comparison on a managed field, so every other patch in this repo returns from the patching
    /// context without ever entering the IL2CPP runtime. These patches had no such gate, read
    /// <c>Time.unscaledTime</c> directly, and recursed to a stack overflow inside
    /// <c>Harmony.PatchAll()</c> — the game could not start. The scope decision was right; what was
    /// missing was that <i>something</i> managed has to be checked first. See
    /// <see cref="EncounterInputGrace.MarkRuntimeReady"/>.</para>
    /// </summary>
    [HarmonyPatch(typeof(LevelupScreen))]
    internal static class LevelupScreenChoiceGuardPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(LevelupScreen.Open))]
        public static void Open_Postfix() => EncounterInputGrace.Arm();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(LevelupScreen.ChooseOffer))]
        public static bool ChooseOffer_Prefix() => !EncounterInputGrace.IsBlocking();

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

    [HarmonyPatch(typeof(ChestWindowUi))]
    internal static class ChestWindowChoiceGuardPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestWindowUi.Open))]
        public static void Open_Postfix() => EncounterInputGrace.Arm();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.ChooseOffer))]
        public static bool ChooseOffer_Prefix() => !EncounterInputGrace.IsBlocking();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.TakeButton))]
        public static bool TakeButton_Prefix() => !EncounterInputGrace.IsBlocking();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.BanishButton))]
        public static bool BanishButton_Prefix() => !EncounterInputGrace.IsBlocking();

        [HarmonyPrefix]
        [HarmonyPatch(nameof(ChestWindowUi.DiscardButton))]
        public static bool DiscardButton_Prefix() => !EncounterInputGrace.IsBlocking();

        // OpenButton is deliberately NOT guarded: opening the chest is the step the player walked
        // over to do, it commits nothing, and blocking it would feel like a dropped input.
    }
}
