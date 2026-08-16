using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Items
{
    [HarmonyPatch(typeof(ItemGhost))]
    internal static class ItemGhostPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        /// <summary>
        /// Skip original interaction if not successful. This prevent a memory access violation somehow when the original code run
        ///
        /// <para><b>And suppress remote players' ghosts — OB-2.</b> The game subscribes <i>every</i>
        /// <c>ItemGhost</c> instance to one static <c>Action&lt;BaseInteractable, bool&gt;</c> on
        /// <c>DetectInteractables</c> in <c>ItemGhost.Init</c>. Remote players carry real
        /// <c>ItemGhost</c> instances in their replicated inventory on this machine, so a local
        /// interaction fires theirs too — and <c>EffectManager.SpawnGhostProjectile</c> takes no
        /// position and reads <c>MyPlayer.Instance.baseDamage</c>, so those ghosts appear on the
        /// <i>local</i> player, scaled by the local player's damage. That is the whole of OB-2: a
        /// player who does not own the item sees ghosts, because the other player's item object is
        /// running on their machine. Full chain in
        /// <c>docs/netplay/08-observed-bugs.md#ob-2</c>.</para>
        ///
        /// <para><b>Not a host-authority problem.</b> Ghosts are <c>EffectManager</c> projectiles and
        /// never touch <c>EnemyManager.SpawnEnemy</c>, so no spawn gate applies to them and none is
        /// missing. The entry originally assumed otherwise.</para>
        ///
        /// <para>The <c>success</c> branch stays first and stays ungated: it is a crash guard, not
        /// netplay logic, and it must keep applying in singleplayer. The session check follows it, so
        /// the ownership test below cannot change singleplayer — where there are no remote items
        /// anyway.</para>
        ///
        /// <para><b>UNVERIFIED.</b> Not run in-game. Whether the non-owner's ghosts also <i>dealt</i>
        /// damage, and so whether this changes damage numbers rather than only visuals, was never
        /// established.</para>
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemGhost.OnInteracted))]
        public static bool OnInteracted_Prefix(bool success, ItemGhost __instance)
        {
            if (!success)
            {
                return false;
            }

            if (!synchronizationService.HasNetplaySessionStarted())
            {
                return true;
            }

            // PassiveAbilityBullseyePatches does this with HasNetplaySessionInitialized; Started is
            // the narrower gate and loses nothing here, because interactions only happen in a run.
            if (playerManagerService.IsRemoteItem(__instance))
            {
                return false;
            }

            return true;
        }
    }
}
