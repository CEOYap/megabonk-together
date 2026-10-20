using Assets.Scripts.Inventory__Items__Pickups.Items;
using MegabonkTogether.Configuration;
using System;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// OB-45. Developer tool: gives this copy of the game the items in <c>[Testing] TestItems</c>
    /// once per multiplayer session, when the run starts, so a test run exercises chosen items
    /// instead of whatever the chests roll. Empty by default, and then does nothing.
    ///
    /// <para>Granted through <c>ItemInventory.AddItem(EItem)</c>, the path a chest uses, so the
    /// mod's item-add postfix replicates each one to every peer exactly like a real pickup.</para>
    /// </summary>
    internal static class TestLoadout
    {
        private static bool granted;

        internal static void Reset() => granted = false;

        internal static void GrantOnce()
        {
            var spec = ModConfig.TestItems?.Value;
            if (granted || string.IsNullOrWhiteSpace(spec))
            {
                return;
            }

            var inventory = GameManager.Instance?.player?.inventory?.itemInventory;
            if (inventory == null)
            {
                Plugin.Log.LogWarning("[test] OB-45: no local item inventory yet; TestItems not granted.");
                return;
            }

            granted = true;

            foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var name = raw;
                var count = 1;
                var star = raw.IndexOf('*');
                if (star > 0)
                {
                    name = raw[..star].Trim();
                    if (!int.TryParse(raw[(star + 1)..].Trim(), out count) || count < 1)
                    {
                        count = 1;
                    }
                }

                if (!Enum.TryParse(name, true, out EItem item))
                {
                    Plugin.Log.LogWarning($"[test] OB-45: '{name}' is not an item name; skipped.");
                    continue;
                }

                for (var i = 0; i < count; i++)
                {
                    inventory.AddItem(item);
                }

                Plugin.Log.LogInfo($"[test] OB-45: granted {item} x{count}.");
            }
        }
    }
}
