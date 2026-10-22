using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;

namespace OmniBonk.Patches.Items
{
    /// <summary>
    /// OB-14. Stops a teammate's damaging items running a second time here. The owner's copy is the
    /// real one and its hits are forwarded; teammate items here are live copies (their <c>Init</c>
    /// subscribes to static events, and the host ticks them), which dealt unattributed damage.
    /// <para>Gated per item, only where decompiled to deal damage (Electric Plug VA 0x180455A20,
    /// Cursed Doll 0x18043FC70, Bob 0x18043A5D0): some ticks may do host-only work. Cf. OB-2.</para>
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    internal static class RemoteItemCopies
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();

        internal static bool InSession => synchronizationService.HasNetplaySessionStarted();

        /// <summary>True for a teammate's item on this machine. Native pointer compare; per-frame safe.</summary>
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
    /// OB-42. Holy Book: only the owner's machine runs it, counting only its own player's heals.
    /// Decompiled: <c>OnHeal</c> (VA 0x18045D100) ignores <c>ph</c>, and <c>Tick</c> (0x18045D250)
    /// damages around <c>MyPlayer.Instance</c>. <b>UNVERIFIED.</b>
    /// </summary>
    [HarmonyPatch(typeof(ItemHolyBook))]
    internal static class ItemHolyBookPatches
    {
        // Prefix-false on purpose in both: the owner's machine runs this item.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(ItemHolyBook.Tick))]
        public static bool Tick_Prefix(ItemHolyBook __instance)
        {
            if (RemoteItemCopies.IsTeammateCopy(__instance))
            {
                return false;
            }

            // OB-48 diagnostic: a book ticking here while we hold none was not recognised as a teammate's.
            if (RemoteItemCopies.InSession && !LocalHoldsHolyBook() && !loggedForeignTick)
            {
                loggedForeignTick = true;
                Plugin.Log.LogInfo(
                    $"[items] OB-48: a Holy Book ticked here though we hold none (owner {EventListenerOwnership.OwnerOf(__instance)?.ToString() ?? "unknown"}).");
            }

            return true;
        }

        private static bool loggedForeignTick;

        internal static bool LocalHoldsHolyBook()
        {
            var items = GameManager.Instance?.player?.inventory?.itemInventory;
            return items != null && items.GetAmount(EItem.HolyBook) > 0;
        }

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

    /// <summary>
    /// OB-48. <c>HolyBookEffect.OnUse</c> listens to a static event (Awake, VA 0x1804983B0), so any
    /// copy played it on our player. Now shown only if we hold a book, like <c>Refresh</c>. Visual only. <b>UNVERIFIED.</b>
    /// </summary>
    [HarmonyPatch(typeof(HolyBookEffect))]
    internal static class HolyBookEffectPatches
    {
        // Prefix-false on purpose: the effect belongs to whoever holds the book.
        [HarmonyPrefix]
        [HarmonyPatch(nameof(HolyBookEffect.OnUse))]
        public static bool OnUse_Prefix()
            => !RemoteItemCopies.InSession || ItemHolyBookPatches.LocalHoldsHolyBook();
    }
}
