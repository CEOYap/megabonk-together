using Assets.Scripts.Actors.Enemies;
using OmniBonk.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Services
{
    /// <summary>
    /// OB-8: logs boss spawn, receipt, max-HP rescales (OB-9) and a 10 s HP/position sample on both
    /// peers, so divergence can be located.
    /// </summary>
    internal static class BossDiagnostics
    {
        private static float lastReportTime = -999f;

        internal static bool IsBoss(Enemy enemy)
            => enemy != null && (enemy.enemyFlag & EEnemyFlag.AnyBoss) != 0;

        internal static void LogEvent(string what, uint id, Enemy enemy, float hp)
        {
            var position = enemy != null ? enemy.transform.position : Vector3.zero;
            var name = enemy != null && enemy.enemyData != null ? enemy.enemyData.enemyName.ToString() : "?";
            Plugin.Log.LogInfo($"[boss] {what}: id {id} {name} hp {hp:F0} at ({position.x:F0}, {position.y:F0}, {position.z:F0}).");
        }

        /// <summary>Called once per frame from the network tick.</summary>
        internal static void Sample(IEnumerable<KeyValuePair<uint, Enemy>> enemies, bool isHost)
        {
            var now = Time.unscaledTime;
            if (now - lastReportTime < Tunables.DIAGNOSTIC_REPORT_INTERVAL_SECONDS)
            {
                return;
            }

            lastReportTime = now;
            foreach (var pair in enemies)
            {
                var enemy = pair.Value;
                if (!IsBoss(enemy))
                {
                    continue;
                }

                var p = enemy.transform.position;
                Plugin.Log.LogInfo(
                    $"[boss] {(isHost ? "host" : "client")} sees id {pair.Key} {enemy.enemyData?.enemyName} " +
                    $"hp {enemy.hp:F0} / {enemy.maxHp:F0} at ({p.x:F0}, {p.y:F0}, {p.z:F0}) flag {enemy.enemyFlag}.");
            }
        }

        internal static void Reset() => lastReportTime = -999f;
    }
}
