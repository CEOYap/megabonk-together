using Assets.Scripts.Actors.Enemies;
using OmniBonk.Common.Models;
using OmniBonk.Helpers;
using OmniBonk.Scripts.Snapshot;

namespace OmniBonk.Extensions
{
    public static class EnemyExtensions
    {
        public static EnemyModel ToModel(this Enemy enemy, uint enemyId)
        {
            // PERF: one access; get_transform is Harmony-patched and this runs per enemy at 40 Hz.
            var transform = enemy.transform;

            return new EnemyModel()
            {
                Id = enemyId,
                Position = Quantizer.Quantize(transform.position),
                Yaw = Quantizer.QuantizeYaw(transform.eulerAngles.y),
                Hp = enemy.hp
            };
        }

        public static EnemySnapshot ToSnapshot(this EnemyModel enemy, double timestamp)
        {
            return new EnemySnapshot()
            {
                Timestamp = timestamp,
                Position = Quantizer.Dequantize(enemy.Position),
                Rotation = Quantizer.Dequantize(enemy.Yaw),
                Hp = enemy.Hp
            };
        }
    }
}
