namespace OmniBonk.Helpers
{
    /// <summary>
    /// Mirrors the cook preconditions of <see cref="InteractableMicrowave.Interact"/>, so a peer that
    /// cannot cook opts out of the shared-experience barrier instead of soft-locking it.
    /// <para><b>CONFIRMED against the dump:</b> <c>CanInteract</c> (VA 0x1804CBCA0) ignores gold;
    /// <c>Interact</c> (VA 0x1804CC180), on the <c>!hasItem</c> branch only, fails silently if gold
    /// &lt; price or fewer than 2 unique items of the rarity.</para>
    /// </summary>
    internal static class MicrowaveHelper
    {
        /// <summary>
        /// Only meaningful when <c>hasItem</c> is false. False if singletons are missing, which opts
        /// out and releases the barrier.
        /// </summary>
        internal static bool CanStartCooking(InteractableMicrowave microwave)
        {
            return CanAfford(microwave) && HasEnoughItemsInRarity(microwave);
        }

        /// <summary>Mirrors the first guard in <c>Interact()</c>.</summary>
        internal static bool CanAfford(InteractableMicrowave microwave)
        {
            var inventory = GetInventory();
            if (microwave == null || inventory == null)
            {
                return false;
            }

            return microwave.GetPrice() <= inventory.gold;
        }

        /// <summary>Mirrors the second guard in <c>Interact()</c>.</summary>
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
