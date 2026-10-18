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
}
