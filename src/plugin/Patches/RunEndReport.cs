using HarmonyLib;
using MegabonkTogether.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using System.Linq;
using RunStats = Assets.Scripts.Saves___Serialization.Progression.Stats.RunStats;

namespace MegabonkTogether.Patches
{
    /// <summary>
    /// OB-45. Writes the end screen's damage panel to the log, one line per row, so a test run can
    /// be checked from the three logs without anyone reading the screens.
    ///
    /// <para><b>Where.</b> <c>GameOverDamageSourcesUi.Start</c> builds the panel from
    /// <c>RunStats.damageSources</c> (the per-source table <c>RunStats.OnEnemyDamaged</c> fills,
    /// OB-14). Each row's LVL is <c>DamageSource.GetLevel</c>, the same call the panel makes, so a
    /// LVL 0 row here is exactly a LVL 0 row on screen. Kills are <c>RunStats.GetStat(kills)</c>.
    /// Once per end screen, in a netplay session only.</para>
    /// </summary>
    [HarmonyPatch(typeof(GameOverDamageSourcesUi))]
    internal static class RunEndReportPatches
    {
        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();

        [HarmonyPostfix]
        [HarmonyPatch("Start")]
        public static void Start_Postfix()
        {
            if (!synchronizationService.HasNetplaySessionInitialized())
            {
                return;
            }

            try
            {
                var rows = new List<(string Source, int Level, float Damage)>();
                var sources = RunStats.damageSources;
                if (sources != null)
                {
                    foreach (var kv in sources)
                    {
                        var ds = kv.Value;
                        if (ds == null)
                        {
                            continue;
                        }

                        rows.Add((ds.damageSource ?? kv.Key, ds.GetLevel(), ds.damage));
                    }
                }

                var me = playerManagerService.GetLocalPlayer()?.ConnectionId;
                var kills = RunStats.GetStat(Assets.Scripts.Saves___Serialization.Progression.Stats.EMyStat.kills);
                var lvl0 = rows.Count(r => r.Level == 0);

                Plugin.Log.LogInfo(
                    $"[report] OB-45: end screen for {me}: {rows.Count} damage row(s), {lvl0} at LVL 0, {kills} kill(s), " +
                    $"host {synchronizationService.IsServerMode() ?? false}.");

                foreach (var row in rows.OrderByDescending(r => r.Damage))
                {
                    var line = string.Format("[report]   {0,-22} LVL {1,3}  {2,14:N0}{3}",
                        row.Source, row.Level, row.Damage, row.Level == 0 ? "  <-- LVL 0" : "");
                    Plugin.Log.LogInfo(line);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[report] OB-45: could not read the damage panel: {ex.Message}");
            }
        }
    }
}
