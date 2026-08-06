namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// The preconditions <see cref="InteractableMicrowave.Interact"/> applies before it will start
    /// a cook, expressed once so the send side and the receive side cannot drift apart.
    ///
    /// <para><b>Why this exists.</b> A microwave in a shared-experience session is a barrier: every
    /// peer has to reach <c>RewardFinished()</c> before the run continues. The receive path used to
    /// call <c>microwave.Interact()</c> for any peer that was alive and passed
    /// <c>CanInteract()</c> — but <c>Interact()</c> <b>silently returns false</b> when that peer
    /// cannot pay, so no encounter window opened, <c>RewardFinished()</c> was never reached, and
    /// the run soft-locked until the 20 s shared-experience failsafe fired. <c>InteractableChest</c>
    /// opts out on <c>!CanAfford()</c> for exactly this reason; the microwave has no
    /// <c>CanAfford()</c>, which is the whole reason the check was missing.</para>
    ///
    /// <para><b>CONFIRMED against the dump</b>, not inferred from the proxy signature.
    /// <c>InteractableMicrowave$$CanInteract</c> (VA <c>0x1804CBCA0</c>) tests only <c>hasItem</c>,
    /// <c>usesLeft &gt; 0</c>, <c>!isCooking</c> and the <c>readyAtTime</c> cooldown — <b>it does
    /// not look at gold</b>. <c>InteractableMicrowave$$Interact</c> (VA <c>0x1804CC180</c>) is where
    /// the price is charged, and only on the <c>!hasItem</c> branch:</para>
    ///
    /// <list type="number">
    ///   <item>gold &lt; <c>GetPrice()</c> → "not enough gold" popup, return false;</item>
    ///   <item><c>GetUniqueItemsInRarity(rarity) &lt; 2</c> → "need more items" popup, return false;</item>
    ///   <item>otherwise open encounter 9.</item>
    /// </list>
    ///
    /// <para>Collecting an already-cooked item (the <c>hasItem</c> branch) charges nothing and has
    /// neither precondition — which is why <see cref="CanStartCooking"/> is only meaningful when
    /// <c>hasItem</c> is false, and why callers must check <c>hasItem</c> first.</para>
    /// </summary>
    internal static class MicrowaveHelper
    {
        /// <summary>
        /// True when this peer could actually start a cook right now: it can pay the price and it
        /// owns at least two unique items of the microwave's rarity. Only meaningful when
        /// <c>microwave.hasItem</c> is false — collecting a cooked item is free.
        ///
        /// <para>Returns <c>false</c> if any of the game singletons on the way to the player's
        /// inventory are missing. That is the safe direction: a peer that "cannot" interact opts
        /// out of the encounter and releases the barrier, rather than hanging everyone else.</para>
        /// </summary>
        internal static bool CanStartCooking(InteractableMicrowave microwave)
        {
            return CanAfford(microwave) && HasEnoughItemsInRarity(microwave);
        }

        /// <summary>Gold check — mirrors the first guard in <c>InteractableMicrowave.Interact()</c>.</summary>
        internal static bool CanAfford(InteractableMicrowave microwave)
        {
            var inventory = GetInventory();
            if (microwave == null || inventory == null)
            {
                return false;
            }

            return microwave.GetPrice() <= inventory.gold;
        }

        /// <summary>Item check — mirrors the second guard in <c>InteractableMicrowave.Interact()</c>.</summary>
        internal static bool HasEnoughItemsInRarity(InteractableMicrowave microwave)
        {
            var inventory = GetInventory();
            if (microwave == null || inventory == null || inventory.itemInventory == null)
            {
                return false;
            }

            return inventory.itemInventory.GetUniqueItemsInRarity(microwave.rarity) >= 2;
        }

        private static PlayerInventory GetInventory()
        {
            var gameManager = GameManager.Instance;
            if (gameManager == null || gameManager.player == null)
            {
                return null;
            }

            return gameManager.player.inventory;
        }
    }
}
