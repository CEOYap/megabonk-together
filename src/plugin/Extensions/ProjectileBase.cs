using Assets.Scripts.Inventory__Items__Pickups.Weapons.Projectiles;
using OmniBonk.Common.Models;
using OmniBonk.Helpers;
using UnityEngine;

namespace OmniBonk.Extensions
{
    public static class ProjectileBaseExtensions
    {
        public static Projectile ToModel(this ProjectileBase projectile, uint projectileId)
        {
            var projectileCringeSword = projectile.GetComponent<ProjectileCringeSword>();
            var projectileHeroSword = projectile.GetComponent<ProjectileHeroSword>();
            var projectileRocket = projectile.GetComponent<ProjectileRocket>();

            var position = projectile.transform.position;
            var rotation = projectile.transform.rotation;
            if (projectileCringeSword != null)
            {
                position = projectileCringeSword.movingProjectile.transform.position;
                rotation = projectileCringeSword.movingProjectile.transform.rotation;
            }

            if (projectileHeroSword != null)
            {
                position = projectileHeroSword.movingProjectile.transform.position;
                rotation = projectileHeroSword.movingProjectile.transform.rotation;
            }

            if (projectileRocket != null)
            {
                position = projectileRocket.rocket.transform.position;
                rotation = projectileRocket.rocket.transform.rotation;
            }

            return new Projectile()
            {
                Id = projectileId,
                Position = Quantizer.Quantize(position),
                FordwardVector = Quantizer.Quantize(rotation * Vector3.forward)
            };
        }
    }
}
