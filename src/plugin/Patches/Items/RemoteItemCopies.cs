using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MegabonkTogether.Patches.Items
{
    /// <summary>
    /// OB-14. Stops a teammate's damaging items from running a second time on this machine.
    ///
    /// <para><b>The owner already runs them.</b> Each player's own game runs their items, and a
    /// client forwards every item hit to the host: <c>Enemy.Damage_Postfix</c> sends any hit whose
    /// source is in <c>AllowedDamageSource</c>, which is every <c>EItem</c> name, and an item's
    /// source is exactly <c>EItem.ToString()</c> (decompiled, <c>ItemElectricPlug..ctor</c> at VA
    /// <c>0x1804561A0</c>). So the owner's copy is the real one and reaches every peer.</para>
    ///
    /// <para><b>Every other peer holds a second, live copy.</b> <c>NetPlayer.AddItem</c> adds each
    /// teammate item to a real <c>ItemInventory</c>, so its <c>Init</c> runs and subscribes to the
    /// game's static events, and on the host <c>NetPlayer.Update</c> calls the teammate's
    /// <c>PlayerInventory.PhysicsTick</c>, which runs <c>ItemInventory.Tick</c> and every item's
    /// <c>Tick</c> (VA <c>0x1804E0680</c>, <c>0x180443500</c>). That copy dealt real damage, with
    /// no attribution, so it also landed on the host's damage panel. The 2026-10-18 end screens
    /// (host: the client's items at LVL 0, more than the owner's own number):</para>
    /// <list type="bullet">
    /// <item><b>Electric Plug</b> 34.1K against 408. <c>Init</c> (VA <c>0x180455A20</c>) subscribes
    /// <c>OnPlayerHit</c> to the static <c>PlayerHealth</c> hit event, so the copy zapped enemies
    /// every time <i>this</i> machine's player was hit.</item>
    /// <item><b>Cursed Doll</b> 27.7K against 14.9K. Damages cursed enemies in <c>Tick</c>
    /// (VA <c>0x18043FC70</c>).</item>
    /// <item><b>Bob (Dead)</b> 23.6K against 3,634. Spawns ghosts in <c>Tick</c>
    /// (VA <c>0x18043A5D0</c>).</item>
    /// </list>
    ///
    /// <para><b>Why these three and not every item's Tick.</b> Some item ticks may do things only
    /// the host is allowed to do (a client cannot spawn pickups), in which case the host's copy is
    /// the only one that works. These three were each decompiled or shown on the end screens to
    /// deal damage; others need the same check before they are gated. Same family as OB-2, which
    /// gated <c>ItemGhost.OnInteracted</c> for the same reason.</para>
    ///
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    internal static class RemoteItemCopies
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        /// <summary>
        /// True when this item belongs to a teammate's inventory on this machine. Compared by native
        /// pointer: the item holds its own inventory, so no search is needed in a per-frame path.
        /// </summary>
        internal static bool InSession => synchronizationService.HasNetplaySessionStarted();

        internal static bool IsTeammateCopy(ItemBase item)
        {
            if (item == null || !synchronizationService.HasNetplaySessionStarted())
            {
                return false;
            }

            var localInventory = GameManager.Instance?.player?.inventory?.itemInventory;
            var owner = item.itemInventoryRef;
            if (localInventory == null || owner == null)
            {
                return false;
            }

            return owner.Pointer != localInventory.Pointer;
        }
    }

    [HarmonyPatch(typeof(ItemElectricPlug))]
    internal static class ItemElectricPlugPatches
    {
        // Prefix-false on purpose: the owner's machine fires this item when the owner is hit.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemElectricPlug.OnPlayerHit))]
        public static bool OnPlayerHit_Prefix(ItemElectricPlug __instance)
            => !RemoteItemCopies.IsTeammateCopy(__instance);
    }

    [HarmonyPatch(typeof(ItemCursedDoll))]
    internal static class ItemCursedDollPatches
    {
        // Prefix-false on purpose: the owner's machine ticks this item and forwards its hits.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemCursedDoll.Tick))]
        public static bool Tick_Prefix(ItemCursedDoll __instance)
            => !RemoteItemCopies.IsTeammateCopy(__instance);
    }

    [HarmonyPatch(typeof(ItemBobDead))]
    internal static class ItemBobDeadPatches
    {
        // Prefix-false on purpose: the owner's machine ticks this item and forwards its hits.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemBobDead.Tick))]
        public static bool Tick_Prefix(ItemBobDead __instance)
            => !RemoteItemCopies.IsTeammateCopy(__instance);
    }

    /// <summary>
    /// OB-42. Holy Book turns its player's healing into damage around that player
    /// ("Healing will now radiate damage to nearby enemies": every 1.5 s, roughly
    /// (8 + level) x 0.5 x healing received, radius (5 + copies) x Size).
    ///
    /// <para><b>Decompiled.</b> <c>OnHeal(PlayerHealth ph, float hpHealed, bool isShield)</c>
    /// (VA 0x18045D100) is a static heal-event handler that adds <c>hpHealed</c> to
    /// <c>healsThisTick</c> and never looks at <c>ph</c>: every copy counted every player's heals.
    /// <c>Tick</c> (0x18045D250) then damages enemies around <c>MyPlayer.Instance</c>, reading
    /// <c>MyPlayer.baseDamage</c> and the Size stat. So a teammate's copy on the host radiated the
    /// host's (and everyone's) healing around the host, unattributed, as the host's damage; and the
    /// owner's own copy also counted healing that was not theirs.</para>
    ///
    /// <para><b>Now</b> it matches Cursed Doll and Bob: only the owner's machine runs it, whose
    /// damage reaches the host through the client's item forward, and it counts only its own
    /// player's heals. <b>UNVERIFIED.</b></para>
    /// </summary>
    [HarmonyPatch(typeof(ItemHolyBook))]
    internal static class ItemHolyBookPatches
    {
        // Prefix-false on purpose in both: the owner's machine runs this item.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemHolyBook.Tick))]
        public static bool Tick_Prefix(ItemHolyBook __instance)
            => !RemoteItemCopies.IsTeammateCopy(__instance);

        [HarmonyPrefix]
        [HarmonyPatch("OnHeal")]
        public static bool OnHeal_Prefix(ItemHolyBook __instance, Assets.Scripts.Inventory__Items__Pickups.PlayerHealth ph)
        {
            if (RemoteItemCopies.IsTeammateCopy(__instance))
            {
                return false;
            }

            return !RemoteItemCopies.InSession || EventListenerOwnership.IsLocalHealth(ph);
        }
    }
}
