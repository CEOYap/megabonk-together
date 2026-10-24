using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive.Implementations;
using Assets.Scripts.Inventory__Items__Pickups.Items.ItemImplementations;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Objects.Pooling;
using HarmonyLib;
using OmniBonk.Helpers;
using UnityEngine;

namespace OmniBonk.Patches.Items
{
    /// <summary>
    /// OB-62. Capture points for <see cref="ItemVisualSync"/>. Each item method below takes a pooled
    /// one-shot effect (decompiled; pool by <c>PoolManager</c> offset): Bonker 0x18043B230 (bonkPool
    /// 0x1E0), Ice Cube 0x18045DBE0 (freezeFxPool 0x1C0), Grandma's Tonic 0x18045BF40 (grandmaTonicPool
    /// 0x198), Quin's Mask 0x1804631A0 (quinMaskPool 0x248), Spicy Meatball 0x1804671E0
    /// (spicyMeatballPool 0x1B0), Snek 0x180464CF0 (snekPool 0x250), Plague 0x18047B730 (poisonPool
    /// 0x128), Bullseye 0x180474EA0 (explosionPool 0x1D8), Cactus 0x18043C750 (cactusPool 0x230),
    /// Cursed Doll 0x18043FC70 (cursedHitPool 0x1F0). Each only runs on one machine already (the host
    /// for procs, the owner for reactions), so capturing every call sends each effect once.
    /// </summary>
    internal static class ItemPoolScopePatches
    {
        private static void Enter(ItemVisualPool pool, out bool __state) => __state = ItemVisualSync.EnterPoolScope(pool);

        private static void Exit(bool __state) => ItemVisualSync.ExitPoolScope(__state);

        [HarmonyPatch(typeof(ItemBonker), nameof(ItemBonker.ProcOnHitEffects))]
        internal static class Bonker
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.Bonk, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(ItemIceCube), nameof(ItemIceCube.ProcOnHitEffects))]
        internal static class IceCube
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.FreezeFx, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(ItemGrandmasSecretTonic), nameof(ItemGrandmasSecretTonic.LateFixedUpdate))]
        internal static class GrandmaTonic
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.GrandmaTonic, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(ItemQuinsMask), nameof(ItemQuinsMask.ProcOnHitEffects))]
        internal static class QuinMask
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.QuinMask, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(ItemSpicyMeatball), nameof(ItemSpicyMeatball.ProcOnHitEffects))]
        internal static class SpicyMeatball
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.SpicyMeatball, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(ItemSnek), nameof(ItemSnek.ProcOnHitEffects))]
        internal static class Snek
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.Snek, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(PassiveAbilityPlague), nameof(PassiveAbilityPlague.OnEnemyDied))]
        internal static class Plague
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.Poison, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(PassiveAbilityBullseye), nameof(PassiveAbilityBullseye.OnEnemyDamaged))]
        internal static class Bullseye
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.Explosion, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(ItemCactus), nameof(ItemCactus.OnTakeDamage))]
        internal static class Cactus
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.Cactus, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }

        [HarmonyPatch(typeof(ItemCursedDoll), nameof(ItemCursedDoll.Tick))]
        internal static class CursedDoll
        {
            private static void Prefix(out bool __state) => Enter(ItemVisualPool.CursedHit, out __state);
            private static void Finalizer(bool __state) => Exit(__state);
        }
    }

    /// <summary>
    /// OB-62. <c>OnTakeFromPool</c> (0x18040ABF0) is the get callback both pool constructors install,
    /// so it names each object an item scope takes. <c>OnReturnedToPool</c> forgets replayed ones.
    /// </summary>
    [HarmonyPatch(typeof(PoolManager))]
    internal static class PoolManagerItemVisualPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch("OnTakeFromPool")]
        private static void OnTakeFromPool_Postfix(GameObject obj) => ItemVisualSync.OnTakeFromPool(obj);

        [HarmonyPostfix]
        [HarmonyPatch("OnReturnedToPool")]
        private static void OnReturnedToPool_Postfix(GameObject obj) => ItemVisualSync.OnReturnedToPool(obj);
    }

    /// <summary>
    /// OB-62. Item hit sparks are the string <c>EnemyHitEffect</c> overload called outside a weapon call.
    /// <c>ItemProjectile.ProjectileDone</c> is left out: tag 85 copies draw their own on the receiver.
    /// Both overloads suspend pool capture, since their <c>AttackHit</c> is pooled too.
    /// </summary>
    [HarmonyPatch(typeof(EffectManager))]
    internal static class ItemHitSparkPatches
    {
        private static int weaponDepth;
        private static int itemProjectileDepth;

        internal static void EnterItemProjectileDone() => itemProjectileDepth++;

        internal static void ExitItemProjectileDone()
        {
            if (itemProjectileDepth > 0)
            {
                itemProjectileDepth--;
            }
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.EnemyHitEffect),
            new[] { typeof(Vector3), typeof(Vector3), typeof(bool), typeof(EWeapon), typeof(GameObject), typeof(bool) })]
        private static void WeaponOverload_Prefix()
        {
            weaponDepth++;
            ItemVisualSync.SuspendPool();
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EffectManager.EnemyHitEffect),
            new[] { typeof(Vector3), typeof(Vector3), typeof(bool), typeof(EWeapon), typeof(GameObject), typeof(bool) })]
        private static void WeaponOverload_Finalizer()
        {
            if (weaponDepth > 0)
            {
                weaponDepth--;
            }

            ItemVisualSync.ResumePool();
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.EnemyHitEffect),
            new[] { typeof(Vector3), typeof(Vector3), typeof(bool), typeof(string), typeof(GameObject), typeof(bool) })]
        private static void StringOverload_Prefix(Vector3 hitPos, Vector3 moveDir, bool hitEnemy, string source, bool useSfx)
        {
            ItemVisualSync.SuspendPool();
            if (weaponDepth == 0 && itemProjectileDepth == 0 && !ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureHitSpark(hitPos, moveDir, hitEnemy, source, useSfx);
            }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(nameof(EffectManager.EnemyHitEffect),
            new[] { typeof(Vector3), typeof(Vector3), typeof(bool), typeof(string), typeof(GameObject), typeof(bool) })]
        private static void StringOverload_Finalizer() => ItemVisualSync.ResumePool();

        /// <summary>Zooma is the only caller (xref).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.ZapEffect))]
        private static void ZapEffect_Prefix(Vector3 pos)
        {
            if (!ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureZap(pos);
            }
        }

        /// <summary>Mirror is the only caller (xref).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(EffectManager.SpawnMirrorFx))]
        private static void SpawnMirrorFx_Prefix(Vector3 pos, Vector3 dir, float size)
        {
            if (!ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureMirror(pos, dir, size);
            }
        }

        /// <summary>
        /// Electric Plug is the only caller (xref). Decompiled 0x1804FBC80: the first call instantiates
        /// <c>electricPlugFx</c> under <c>MyPlayer</c> (it plays on enable), later calls play it.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(nameof(EffectManager.ElectricalPlugEffect))]
        private static void ElectricalPlugEffect_Postfix(EffectManager __instance)
        {
            if (ItemVisualSync.IsReplaying || !ItemVisualSync.IsSessionActive)
            {
                return;
            }

            var fx = __instance.electricPlugEffect;
            ItemVisualSync.CapturePlayerFx(ItemPlayerFx.ElectricPlug, fx != null ? fx.transform.localScale : Vector3.one);
        }
    }

    [HarmonyPatch(typeof(ItemProjectile), nameof(ItemProjectile.ProjectileDone))]
    internal static class ItemProjectileDoneScopePatches
    {
        private static void Prefix() => ItemHitSparkPatches.EnterItemProjectileDone();
        private static void Finalizer() => ItemHitSparkPatches.ExitItemProjectileDone();
    }

    /// <summary>
    /// OB-62. The five gloves (decompiled, e.g. Power 0x18045A980) instantiate their EM prefab once as
    /// <c>fx</c>, parent it under <c>MyPlayer.feet</c> at up x 0.01, set its local scale to the radius
    /// and call <c>EffectPlayer.Play</c>. The host runs teammates' gloves, so it also drew them on its
    /// own player; the play is captured, and on the host a teammate's <c>fx</c> is moved to them.
    /// </summary>
    internal static class GlovePatches
    {
        private static ItemPlayerFx scope;

        private static void Enter(ItemPlayerFx fx) => scope = fx;

        private static void Exit() => scope = 0;

        internal static void OnPlay(EffectPlayer player)
        {
            if (scope == 0 || player == null || ItemVisualSync.IsReplaying || !ItemVisualSync.IsSessionActive)
            {
                return;
            }

            ItemVisualSync.ReparentToOwner(player.transform);
            ItemVisualSync.CapturePlayerFx(scope, player.transform.localScale);
        }

        [HarmonyPatch(typeof(ItemGlovesLightning), nameof(ItemGlovesLightning.ProcOnHitEffects))]
        internal static class Lightning
        {
            private static void Prefix() => Enter(ItemPlayerFx.GloveLightning);
            private static void Finalizer() => Exit();
        }

        [HarmonyPatch(typeof(ItemGlovesPoison), nameof(ItemGlovesPoison.ProcOnHitEffects))]
        internal static class Poison
        {
            private static void Prefix() => Enter(ItemPlayerFx.GlovePoison);
            private static void Finalizer() => Exit();
        }

        [HarmonyPatch(typeof(ItemGlovesBlood), nameof(ItemGlovesBlood.ProcOnHitEffects))]
        internal static class Blood
        {
            private static void Prefix() => Enter(ItemPlayerFx.GloveBlood);
            private static void Finalizer() => Exit();
        }

        [HarmonyPatch(typeof(ItemGlovesCursed), nameof(ItemGlovesCursed.ProcOnHitEffects))]
        internal static class Cursed
        {
            private static void Prefix() => Enter(ItemPlayerFx.GloveCurse);
            private static void Finalizer() => Exit();
        }

        [HarmonyPatch(typeof(ItemGlovesPower), nameof(ItemGlovesPower.ProcOnHitEffects))]
        internal static class Power
        {
            private static void Prefix() => Enter(ItemPlayerFx.GlovePower);
            private static void Finalizer() => Exit();
        }
    }

    [HarmonyPatch(typeof(EffectPlayer), nameof(EffectPlayer.Play))]
    internal static class EffectPlayerItemVisualPatches
    {
        private static void Prefix(EffectPlayer __instance) => GlovePatches.OnPlay(__instance);
    }

    /// <summary>
    /// OB-62. Toxic Barrel's <c>Activate</c> (0x180468080) raises the static <c>A_OnUse</c>, which the
    /// player's <c>ToxicBarrelEffect.OnUse</c> (0x180498A20) answers: scale its <c>effectPlayer</c> by the
    /// radius and play it. Captured after <c>Activate</c>, not in <c>OnUse</c>: IL2CPP folded
    /// <c>ToxicBarrelEffect.OnUse</c> and <c>HolyBookEffect.OnUse</c> into one function (same VA), and a
    /// patch on it made every Holy Book use recurse until the stack overflowed (hands-off, 2026-10-25).
    /// </summary>
    [HarmonyPatch(typeof(ItemToxicBarrel), nameof(ItemToxicBarrel.Activate))]
    internal static class ToxicBarrelVisualPatches
    {
        private static ToxicBarrelEffect effect;

        private static void Postfix()
        {
            if (ItemVisualSync.IsReplaying || !ItemVisualSync.IsSessionActive)
            {
                return;
            }

            if (effect == null)
            {
                var player = GameManager.Instance?.player;
                var found = player != null ? player.gameObject.RuntimeGetComponentsInChildren<ToxicBarrelEffect>(true) : null;
                effect = found != null && found.Length > 0 ? found[0] : null;
            }

            var fx = effect != null ? effect.effectPlayer : null;
            ItemVisualSync.CapturePlayerFx(ItemPlayerFx.ToxicBarrel, fx != null ? fx.transform.localScale : Vector3.one);
        }
    }

    /// <summary>
    /// OB-62. Slutty Cannon (<c>ProcOnHitEffects</c> 0x1804646F0) takes <c>rocketPool</c> and calls
    /// <c>Rocket.Set</c> (0x1804F4220) with <c>weaponBase</c> null. A copy on the receiver flies but never
    /// collides (<c>CheckCollision</c> 0x1804F3300 → <c>HitEnemy</c> 0x1804F3CB0 would damage), and
    /// ends only when the sender's rocket does, through its own <c>ProjectileDone</c> (0x1804F3FA0),
    /// which draws the explosion.
    /// </summary>
    [HarmonyPatch(typeof(Rocket))]
    internal static class ItemRocketVisualPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(Rocket.Set))]
        private static void Set_Postfix(Rocket __instance, WeaponBase weaponBase)
        {
            if (weaponBase == null && !ItemVisualSync.IsReplaying && ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureRocketSpawn(__instance);
            }
        }

        /// <summary>Prefix-false for a copy: it must never hit.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Rocket.CheckCollision))]
        private static bool CheckCollision_Prefix(Rocket __instance, ref bool __result)
        {
            if (!ItemVisualSync.IsRocketCopy(__instance))
            {
                return true;
            }

            __result = false;
            return false;
        }

        /// <summary>Prefix-false for a copy, which ends only when the sender's rocket does.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Rocket.ProjectileDone))]
        private static bool ProjectileDone_Prefix(Rocket __instance)
        {
            if (!ItemVisualSync.AllowCopyDone(__instance))
            {
                return false;
            }

            if (ItemVisualSync.IsSessionActive)
            {
                ItemVisualSync.CaptureRocketDone(__instance);
            }

            return true;
        }
    }

    /// <summary>
    /// OB-62. Prefix-false for a replayed pooled object, in case its prefab carries a
    /// <c>DamageZone</c> (0x18048C450 damages in <c>FixedUpdate</c>): a copy must never damage.
    /// </summary>
    [HarmonyPatch(typeof(DamageZone), nameof(DamageZone.FixedUpdate))]
    internal static class DamageZoneReplayPatches
    {
        private static bool Prefix(DamageZone __instance) => !ItemVisualSync.IsReplayedObject(__instance.transform.root.gameObject);
    }
}
