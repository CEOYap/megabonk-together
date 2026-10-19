using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using HarmonyLib;

namespace MegabonkTogether.Patches.Items
{
    /// <summary>
    /// OB-43. The rest of the items whose effect belongs to their owner's game, gated the same way
    /// as Electric Plug, Cursed Doll, Bob and Holy Book (<see cref="RemoteItemCopies"/>).
    ///
    /// <para><b>Why.</b> Every machine holds a live copy of every player's items, subscribed to the
    /// game's static player events, and the host also ticks every teammate's copy
    /// (<c>NetPlayer.Update</c> -> <c>PlayerInventory.PhysicsTick</c>). An audit of all 86 items'
    /// decompiled bodies, cross-checked against megabonkinfo.org, finds three kinds that must run
    /// only on their owner's game:</para>
    /// <list type="bullet">
    /// <item><b>Reactions to the player</b>: Cactus, Toxic Barrel, Golden Shield, Speed Boi and
    /// Cowards Cloak on taking damage; Mirror's damage block and reflect; Phantom Shroud on
    /// evading; Flappy Feathers on jumping; Scarf on landing; both Credit Cards on a chest window;
    /// Bloody Cleaver on a lifesteal proc; Gasmask on a stage start. A teammate's copy reacted to
    /// THIS machine's player: a teammate's Golden Shield paid the host gold when the host was hit, a
    /// teammate's Mirror could block the host's damage, a teammate's Cactus retaliated for the host
    /// (and, on a client, was forwarded as the teammate's hit), a teammate's Credit Card changed the
    /// host's chest price.</item>
    /// <item><b>Player effects on a timer</b>: Beefy Ring, Campfire, Idle Juice, Rollerblades,
    /// Golden Sneakers, Gamer Goggles, Sucky Hoof, Cowards Cloak, Phantom Shroud, Spiky Shield,
    /// Gasmask, Mirror, Kevin, Demonic Blood and Demonic Soul. Their state follows the owner's
    /// movement, health or stacks, which only the owner's game has; Kevin's tick damages "the
    /// player".</item>
    /// <item><b>Damage on a timer</b>: Energy Core (<c>FireOrb</c>) and Bob's Lantern
    /// (<c>Explode</c>). The owner's game fires them and forwards the hits (their sources are EItem
    /// names), so the host's copy fired a second set.</item>
    /// </list>
    /// <para>Handlers that receive a <c>PlayerHealth</c> also ignore any player but their own.</para>
    ///
    /// <para><b>Not gated, on purpose:</b> the ticks of on-hit items (Big Bonk, the Mitts, Joe's
    /// Dagger, Lightning Orb, Moldy Cheese, Quin's Mask, Slutty Cannon, Snek, Spicy Meatball, Weeb
    /// Headset, Grandma's Tonic, Bloody Cleaver) reset per-tick proc caps and cooldowns. Since OB-37
    /// the host runs those items for their owner, so the host's copy must keep ticking.</para>
    ///
    /// <para><b>UNVERIFIED.</b> Not run in-game.</para>
    /// </summary>
    internal static class OwnerOnlyItems
    {
        internal static bool OwnerOnly(Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase item)
            => !RemoteItemCopies.IsTeammateCopy(item);

        internal static bool OwnerOnly(Assets.Scripts.Inventory__Items__Pickups.Items.ItemBase item, PlayerHealth ph)
            => !RemoteItemCopies.IsTeammateCopy(item)
               && (!RemoteItemCopies.InSession || ph == null || EventListenerOwnership.IsLocalHealth(ph));
    }

    // Prefix-false throughout is deliberate: the owner's game runs each of these.

    [HarmonyPatch(typeof(ItemCactus))]
    internal static class ItemCactusOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnTakeDamage")]
        public static bool OnTakeDamage_Prefix(ItemCactus __instance, PlayerHealth ph) => OwnerOnlyItems.OwnerOnly(__instance, ph);
    }

    [HarmonyPatch(typeof(ItemToxicBarrel))]
    internal static class ItemToxicBarrelOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnTakeDamage")]
        public static bool OnTakeDamage_Prefix(ItemToxicBarrel __instance, PlayerHealth ph) => OwnerOnlyItems.OwnerOnly(__instance, ph);
    }

    [HarmonyPatch(typeof(ItemGoldenShield))]
    internal static class ItemGoldenShieldOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnPlayerTakeDamage")]
        public static bool OnPlayerTakeDamage_Prefix(ItemGoldenShield __instance, PlayerHealth playerHealth) => OwnerOnlyItems.OwnerOnly(__instance, playerHealth);
    }

    [HarmonyPatch(typeof(ItemSpeedBoi))]
    internal static class ItemSpeedBoiOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnTakeDamage")]
        public static bool OnTakeDamage_Prefix(ItemSpeedBoi __instance, PlayerHealth ph) => OwnerOnlyItems.OwnerOnly(__instance, ph);
    }

    [HarmonyPatch(typeof(ItemCowardsCloak))]
    internal static class ItemCowardsCloakOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnDamage")]
        public static bool OnDamage_Prefix(ItemCowardsCloak __instance, PlayerHealth ph) => OwnerOnlyItems.OwnerOnly(__instance, ph);

        [HarmonyPrefix, HarmonyPatch(nameof(ItemCowardsCloak.Tick))]
        public static bool Tick_Prefix(ItemCowardsCloak __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemMirror))]
    internal static class ItemMirrorOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnCheckStopDamage")]
        public static bool OnCheckStopDamage_Prefix(ItemMirror __instance) => OwnerOnlyItems.OwnerOnly(__instance);

        // Returns false (nothing reflected) for a teammate's copy.
        [HarmonyPrefix, HarmonyPatch("ReflectDamage")]
        public static bool ReflectDamage_Prefix(ItemMirror __instance, ref bool __result)
        {
            if (OwnerOnlyItems.OwnerOnly(__instance))
            {
                return true;
            }

            __result = false;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(ItemMirror.Tick))]
        public static bool Tick_Prefix(ItemMirror __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemPhantomShroud))]
    internal static class ItemPhantomShroudOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnEvade")]
        public static bool OnEvade_Prefix(ItemPhantomShroud __instance) => OwnerOnlyItems.OwnerOnly(__instance);

        [HarmonyPrefix, HarmonyPatch(nameof(ItemPhantomShroud.Tick))]
        public static bool Tick_Prefix(ItemPhantomShroud __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemFlappyFeathers))]
    internal static class ItemFlappyFeathersOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnJumped")]
        public static bool OnJumped_Prefix(ItemFlappyFeathers __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemScarf))]
    internal static class ItemScarfOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnGroundedChange")]
        public static bool OnGroundedChange_Prefix(ItemScarf __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemCreditCardGreen))]
    internal static class ItemCreditCardGreenOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnChestWindowOpen")]
        public static bool OnChestWindowOpen_Prefix(ItemCreditCardGreen __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemCreditCardRed))]
    internal static class ItemCreditCardRedOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnChestWindowOpen")]
        public static bool OnChestWindowOpen_Prefix(ItemCreditCardRed __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemBloodyCleaver))]
    internal static class ItemBloodyCleaverOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnLifestealProc")]
        public static bool OnLifestealProc_Prefix(ItemBloodyCleaver __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemGasmask))]
    internal static class ItemGasmaskOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch("OnStageStarted")]
        public static bool OnStageStarted_Prefix(ItemGasmask __instance) => OwnerOnlyItems.OwnerOnly(__instance);

        [HarmonyPrefix, HarmonyPatch(nameof(ItemGasmask.Tick))]
        public static bool Tick_Prefix(ItemGasmask __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    // Ticks only: player effects on a timer, and the two timed damage items.

    [HarmonyPatch(typeof(ItemBeefyRing))]
    internal static class ItemBeefyRingOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemBeefyRing.Tick))]
        public static bool Tick_Prefix(ItemBeefyRing __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemCampfire))]
    internal static class ItemCampfireOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemCampfire.Tick))]
        public static bool Tick_Prefix(ItemCampfire __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemIdleJuice))]
    internal static class ItemIdleJuiceOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemIdleJuice.Tick))]
        public static bool Tick_Prefix(ItemIdleJuice __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemRollerblades))]
    internal static class ItemRollerbladesOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemRollerblades.Tick))]
        public static bool Tick_Prefix(ItemRollerblades __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemGoldenSneakers))]
    internal static class ItemGoldenSneakersOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemGoldenSneakers.Tick))]
        public static bool Tick_Prefix(ItemGoldenSneakers __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemGamerGoggles))]
    internal static class ItemGamerGogglesOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemGamerGoggles.Tick))]
        public static bool Tick_Prefix(ItemGamerGoggles __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemSuckyHoof))]
    internal static class ItemSuckyHoofOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Assets.Scripts.Inventory__Items__Pickups.Items.ItemSuckyHoof.Tick))]
        public static bool Tick_Prefix(Assets.Scripts.Inventory__Items__Pickups.Items.ItemSuckyHoof __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemSpikyShield))]
    internal static class ItemSpikyShieldOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemSpikyShield.Tick))]
        public static bool Tick_Prefix(ItemSpikyShield __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemKevin))]
    internal static class ItemKevinOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemKevin.Tick))]
        public static bool Tick_Prefix(ItemKevin __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemDemonicBlood))]
    internal static class ItemDemonicBloodOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemDemonicBlood.Tick))]
        public static bool Tick_Prefix(ItemDemonicBlood __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemDemonicSoul))]
    internal static class ItemDemonicSoulOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemDemonicSoul.Tick))]
        public static bool Tick_Prefix(ItemDemonicSoul __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemEnergyCore))]
    internal static class ItemEnergyCoreOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemEnergyCore.Tick))]
        public static bool Tick_Prefix(ItemEnergyCore __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }

    [HarmonyPatch(typeof(ItemBobLantern))]
    internal static class ItemBobLanternOwnerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ItemBobLantern.Tick))]
        public static bool Tick_Prefix(ItemBobLantern __instance) => OwnerOnlyItems.OwnerOnly(__instance);
    }
}
