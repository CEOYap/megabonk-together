using Assets.Scripts.Actors.Enemies;
using MegabonkTogether.Configuration;
using System.Collections.Generic;
using UnityEngine;

namespace MegabonkTogether.Services
{
    /// <summary>
    /// OB-8. Puts every step of a boss's life on both peers into the log, so the host's and a
    /// client's line for the same moment can be compared.
    ///
    /// <para><b>Why.</b> On 2026-10-19 the Desert final boss read 6.8M on the host and 5.5M on the
    /// client, never moved on the client, and neither log said anything about it: no spawn line, no
    /// HP, no rescale, no position. OB-7 and OB-8 were both marked fixed after runs that happened to
    /// work, with no code change; this exists so the next run says which step diverges instead.</para>
    ///
    /// <para>Logs: the host's spawn message (id, HP sent, flag), the client's receipt (HP applied), every
    /// max-HP rescale sent and applied (OB-9), and every 10 s each live boss's id, HP / max and
    /// position. Bosses only, so the cost is a dictionary walk every 10 s and a handful of lines a
    /// stage.</para>
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
