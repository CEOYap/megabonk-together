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
    /// affected.</para>
    ///
    /// <para><b>What the guard covers now.</b> Arming moved to <c>ShowLevelupScreen</c> and
    /// <c>OpeningFinished</c>, both non-virtual, so no virtual method is patched anywhere in this
    /// file. Guarded: the chest's <c>TakeButton</c>, <c>BanishButton</c> and <c>DiscardButton</c> —
    /// its three irreversible commits — plus <c>Skip</c>, <c>Banish</c> and <c>Leave</c> on the
    /// level-up screen. <b>Not guarded: <c>ChooseOffer</c> on either type</b>, because it is
    /// virtual. On the chest that is minor, since the real commits are the buttons. On the level-up
    /// screen it is the item pick itself, which is the accident this feature was written for — so
    /// the feature is partial by construction, and that gap closes only if the virtual-patching
    /// question is solved.</para>
    ///
    /// <para><b>UNVERIFIED:</b> that <i>every</i> virtual method is unpatchable this way, rather
    /// than something narrower about these two. The correlation is exact across this repo but the
    /// Il2CppInterop internals have not been read; the six non-virtual patches are left in place
    /// partly to keep that distinction testable.</para>
    /// </summary>
    [HarmonyPatch(typeof(LevelupScreen))]
    internal static class LevelupScreenChoiceGuardPatches
    {
        /// <summary>
        /// Arms the guard. <c>ShowLevelupScreen</c> is non-virtual, so unlike <c>Open</c> it can be
        /// patched — see the class comment.
        ///
        /// <para><b>UNVERIFIED:</b> that this runs on every level-up window and at the moment the
        /// window becomes interactive. The name and the non-virtual signature are all the interop
        /// metadata can tell us; the body is a stub. If it fires late the guard is short, if it
        /// never fires the guard is inert — both degrade to today's behaviour rather than breaking
        /// anything, which is why this is worth shipping unverified.</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(LevelupScreen.ShowLevelupScreen))]
        public static void ShowLevelupScreen_Postfix() => EncounterInputGrace.Arm();

        // LevelupScreen.ChooseOffer stays unpatched and unguarded: it is virtual, and it is the
        // level-up item pick — the exact accident e17c6ff was written for. Skip, Banish and Leave
        // below are non-virtual and are guarded, so what remains unprotected is choosing an item,
        // not discarding the window. That gap is the cost of not being able to patch a virtual
        // method here, and it is stated rather than hidden.

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
        /// <summary>
        /// Arms the guard. <c>OpeningFinished</c> is non-virtual, and is the point the chest's
        /// opening animation ends and the offer becomes actionable — which is the moment that
        /// matters here, later and therefore tighter than <c>Open</c> would have been.
        ///
        /// <para><b>UNVERIFIED:</b> the same caveat as <c>ShowLevelupScreen_Postfix</c>. Fails
        /// toward an inert guard, not toward a stranded player.</para>
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(ChestWindowUi.OpeningFinished))]
        public static void OpeningFinished_Postfix() => EncounterInputGrace.Arm();

        // ChestWindowUi.ChooseOffer stays unpatched for the same reason as LevelupScreen's. It
        // matters less here: the chest's irreversible commits are TakeButton, BanishButton and
        // DiscardButton, all non-virtual and all guarded below.

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
