// Debug builds only. See docs/netplay/05-local-testing.md.
#if DEBUG
using Assets.Scripts.Inventory__Items__Pickups.Items;
using OmniBonk.Configuration;
using System;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-45: grants <c>[Testing] TestItems</c> once per session at run start, via
    /// <c>ItemInventory.AddItem</c> so they replicate like real pickups.
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
#endif
