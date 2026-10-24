using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using UnityEngine;

namespace OmniBonk.Patches.Projectiles
{
    /// <summary>
    /// OB-61. The size setup a client skips: it instantiates a received projectile from the prefab and
    /// never runs <c>WeaponAttack.SpawnProjectile</c>, <c>ProjectileBase.Set</c> or <c>TryInit</c>.
    /// Decompiled:
    /// <list type="bullet">
    /// <item><c>SpawnProjectile</c> (0x1804506F0) sets the root <c>localScale</c> to the prefab's
    /// times <c>GetSize()</c> (0x1804503A0): (attack size x <c>projectileSizeMultiplier</c> -
    /// <c>projectileSizeMultiplier</c>) + 1.</item>
    /// <item><c>Set</c> (0x18042C870) seeds <c>baseProjectileRadius</c> (0x34) from
    /// <c>projectileRadius</c> (0x30) when it is 0, then sets <c>projectileRadius</c> = base x
    /// <c>GetSize()</c>.</item>
    /// <item><c>TryInit</c>: Arrow (0x1804E0CA0) and Bananarang (0x1804E3970) set the trail's start width
    /// to its prefab width x attack size; Axe (0x1804E1990) to attack size x 0.25 (.rdata
    /// 0x18262EC44).</item>
    /// </list>
    /// Attack size is <c>WeaponUtility.GetAttackSizeMultiplier</c>, which <c>WeaponUtilityPatches</c>
    /// already answers with the owner's synced stat (OB-1), so a client computes the host's value.
    /// Mines (<see cref="ProjectileMinesPatches.InitReceived"/>), Poison Flask (its <c>TryInit</c> resets
    /// the scale to one; its explosion scale comes from the host) and rockets keep their own setup.
    /// </summary>
    internal static class ReceivedProjectileSize
    {
        private const float AXE_TRAIL_WIDTH = 0.25f;

        internal static void Apply(ProjectileBase projectile, WeaponAttack attack, WeaponBase weapon)
        {
            if (projectile == null || attack == null || weapon == null)
            {
                return;
            }

            var size = attack.GetSize();
            Helpers.ProjectileSizeDiagnostics.Applied(size);

            if (projectile.baseProjectileRadius <= 0f)
            {
                projectile.baseProjectileRadius = projectile.projectileRadius;
            }

            projectile.projectileRadius = projectile.baseProjectileRadius * size;

            if (projectile.GetComponent<ProjectileMines>() != null
                || projectile.GetComponent<ProjectilePoisonFlask>() != null
                || projectile.GetComponent<ProjectileRocket>() != null)
            {
                return;
            }

            projectile.transform.localScale *= size;

            var attackSize = WeaponUtility.GetAttackSizeMultiplier(weapon);

            var arrow = projectile.GetComponent<ProjectileArrow>();
            if (arrow != null && arrow.trailRenderer != null)
            {
                arrow.trailRenderer.startWidth *= attackSize;
                return;
            }

            var banana = projectile.GetComponent<ProjectileBanana>();
            if (banana != null && banana.trailRenderer != null)
            {
                banana.trailRenderer.startWidth *= attackSize;
                return;
            }

            var axe = projectile.GetComponent<ProjectileAxe>();
            if (axe != null && axe.trailRenderer != null)
            {
                axe.trailRenderer.startWidth = attackSize * AXE_TRAIL_WIDTH;
            }
        }
    }
}
