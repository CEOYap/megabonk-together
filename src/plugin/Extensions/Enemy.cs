using Assets.Scripts.Actors.Enemies;
using MegabonkTogether.Common.Models;
using MegabonkTogether.Helpers;
using MegabonkTogether.Scripts.Snapshot;

namespace MegabonkTogether.Extensions
{
    public static class EnemyExtensions
    {
        public static EnemyModel ToModel(this Enemy enemy, uint enemyId)
        {
            // PERF: one `enemy.transform` instead of two. Component.get_transform is patched by
            // this mod, so each access is a Harmony detour on top of the native property — and
            // this runs per enemy, 40 times a second, on the host.
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
