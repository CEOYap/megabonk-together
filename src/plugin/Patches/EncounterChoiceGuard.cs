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
    /// <para><b>The paragraph above is kept because its conclusion still stands. The paragraph
    /// below it was my first explanation of the launch crash, and it was wrong; it is kept under
    /// the same rule.</b></para>
    ///
    /// <para><b>WRONG — first diagnosis.</b> "It treated <c>HasNetplaySessionStarted()</c> as purely
    /// a scope decision. It is also, accidentally, load-order protection: an enum comparison on a
    /// managed field, so every other patch returns from the patching context without entering the
    /// IL2CPP runtime. These patches had no such gate, read <c>Time.unscaledTime</c> directly, and
    /// recursed to a stack overflow." That change was made and the game still would not launch. The
    /// second trace had our frames <i>absent</i> from the loop entirely, which falsified it:
    /// <c>Time.unscaledTime</c> was a participant in the first trace, not its cause. The managed
    /// gate is still in place — it is correct on its own terms and cheap — but it fixed nothing.</para>
    ///
    /// <para><b>Actual cause.</b> <c>Open</c> and <c>ChooseOffer</c> are <b>virtual overrides</b>
    /// (<c>reuseslot virtual</c> on both concrete types, over <c>newslot virtual</c> on
    /// <c>BaseEncounterWindow</c>). Under Il2CppInterop's Harmony support, invoking the original of
    /// a patched virtual method re-dispatches through the vtable slot the detour just replaced, so
    /// the patch calls itself: <c>DMD&lt;ChooseOffer&gt; → il2cpp_runtime_invoke →
    /// OnInvokeMethod → (il2cpp→managed) ChooseOffer → DMD&lt;ChooseOffer&gt;</c>, unbounded,
    /// inside <c>PatchAll</c>. Every one of this repo's ~70 working patches is non-virtual;
    /// these four were the only virtual ones, which is exactly why this was the only class
    /// affected. The four are disabled below.</para>
    ///
    /// <para><b>UNVERIFIED:</b> that <i>every</i> virtual method is unpatchable this way, rather
    /// than something narrower about these two. The correlation is exact across this repo but the
    /// Il2CppInterop internals have not been read; the six non-virtual patches are left in place
    /// partly to keep that distinction testable.</para>
    /// </summary>
    [HarmonyPatch(typeof(LevelupScreen))]
    internal static class LevelupScreenChoiceGuardPatches
    {
        // DISABLED — Open and ChooseOffer are virtual overrides, and patching a virtual method
        // here recurses until the stack overflows, inside Harmony.PatchAll(). See the class
        // comment above. The guard is inert without an arming hook; that is deliberate for now.
        //
        // [HarmonyPostfix]
        // [HarmonyPatch(nameof(LevelupScreen.Open))]
        // public static void Open_Postfix() => EncounterInputGrace.Arm();
        //
        // [HarmonyPrefix]
        // [HarmonyPatch(nameof(LevelupScreen.ChooseOffer))]
        // public static bool ChooseOffer_Prefix() => !EncounterInputGrace.IsBlocking();

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
        // DISABLED — same reason as LevelupScreen above: both are virtual overrides.
        // ChestWindowUi.ChooseOffer is the exact method the overflow trace names.
        //
        // [HarmonyPostfix]
        // [HarmonyPatch(nameof(ChestWindowUi.Open))]
        // public static void Open_Postfix() => EncounterInputGrace.Arm();
        //
        // [HarmonyPrefix]
        // [HarmonyPatch(nameof(ChestWindowUi.ChooseOffer))]
        // public static bool ChooseOffer_Prefix() => !EncounterInputGrace.IsBlocking();

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
