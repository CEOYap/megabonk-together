// Debug builds only. See docs/netplay/05-local-testing.md.
#if DEBUG
using Assets.Scripts.Inventory__Items__Pickups.Items;
using OmniBonk.Configuration;
using System;
using System.Collections;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-45: grants <c>[Testing] TestItems</c> once per session at run start, via
    /// <c>ItemInventory.AddItem</c> so they replicate like real pickups, and <c>TestWeapons</c> via
    /// <c>WeaponInventory.AddWeapon</c>, whose postfix sends <c>WeaponAdded</c> as a level-up pick does.
    /// <para>Granted <see cref="GRANT_DELAY_SECONDS"/> after run start: a grant at the instant the run
    /// starts reached a client before it had spawned the host's player, which dropped it ("Player not
    /// found ... OnReceivedWeaponAdded"), so the client ignored the host's rockets for two minutes.</para>
    /// </summary>
    internal static class TestLoadout
    {
        private static bool granted;

        private const float GRANT_DELAY_SECONDS = 5f;

        internal static void Reset()
        {
            granted = false;
            spawnedEnemies = false;
        }

        internal static void GrantOnce()
        {
            SpawnTestEnemiesOnce();

            var spec = ModConfig.TestItems?.Value ?? "";
            if (granted || (string.IsNullOrWhiteSpace(spec) && string.IsNullOrWhiteSpace(ModConfig.TestWeapons?.Value)))
            {
                return;
            }

            granted = true;
            CoroutineRunner.Instance.Run(GrantAfterPeersSpawn(spec));
        }

        private static bool spawnedEnemies;

        /// <summary>
        /// Host, <c>TestSpawnEnemies</c>: requests that many skeletons through the summoner overload of
        /// <c>SpawnEnemy</c>, the one both caps apply to, 20 a frame. Logs requested, spawned and the
        /// peak alive count, so a run shows where the cap stopped them.
        /// </summary>
        private static void SpawnTestEnemiesOnce()
        {
            var count = ModConfig.TestSpawnEnemies?.Value ?? 0;
            var sync = Plugin.Services.GetService(typeof(Services.ISynchronizationService)) as Services.ISynchronizationService;
            if (spawnedEnemies || count <= 0 || sync?.IsServerMode() != true)
            {
                return;
            }

            spawnedEnemies = true;
            CoroutineRunner.Instance.Run(SpawnTestEnemies(count));
        }

        private static IEnumerator SpawnTestEnemies(int count)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(30f);

            var manager = Assets.Scripts.Managers.EnemyManager.Instance;
            var data = DataManager.Instance?.GetEnemyData(Actors.Enemies.EEnemy.Skeleton);
            if (manager == null || data == null)
            {
                Plugin.Log.LogWarning("[enemy-cap] test spawn skipped: no EnemyManager or skeleton data.");
                yield break;
            }

            var spawned = 0;
            var peak = manager.numEnemies;
            for (var i = 0; i < count; i++)
            {
                if (manager.SpawnEnemy(data, 0, false, 0, true) != null)
                {
                    spawned++;
                }

                if (manager.numEnemies > peak)
                {
                    peak = manager.numEnemies;
                }

                if (i % 20 == 19)
                {
                    yield return null;
                }
            }

            Plugin.Log.LogInfo($"[enemy-cap] test spawn: requested {count}, spawned {spawned}, peak alive {peak}, max reported {manager.GetNumMaxEnemies()}.");
        }

        private static IEnumerator GrantAfterPeersSpawn(string spec)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(GRANT_DELAY_SECONDS);

            var inventory = GameManager.Instance?.player?.inventory?.itemInventory;
            if (inventory == null)
            {
                Plugin.Log.LogWarning("[test] OB-45: no local item inventory; TestItems and TestWeapons not granted.");
                yield break;
            }

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

            GrantWeapons();
        }

        private static void GrantWeapons()
        {
            var spec = ModConfig.TestWeapons?.Value;
            var weapons = GameManager.Instance?.player?.inventory?.weaponInventory;
            if (string.IsNullOrWhiteSpace(spec) || weapons == null)
            {
                return;
            }

            foreach (var name in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse(name, true, out EWeapon weapon)
                    || DataManager.Instance == null
                    || !DataManager.Instance.weapons.TryGetValue(weapon, out var data))
                {
                    Plugin.Log.LogWarning($"[test] '{name}' is not a weapon name; skipped.");
                    continue;
                }

                if (weapons.weapons.ContainsKey(weapon))
                {
                    continue;
                }

                weapons.AddWeapon(data, new Il2CppSystem.Collections.Generic.List<Assets.Scripts.Inventory__Items__Pickups.Stats.StatModifier>());
                Plugin.Log.LogInfo($"[test] granted weapon {weapon}.");
            }
        }
    }
}
#endif
