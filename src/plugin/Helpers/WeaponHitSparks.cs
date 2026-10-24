using Assets.Scripts.Inventory__Items__Pickups.Weapons.Attacks;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-60. Weapon hit sparks come from the host. Every weapon spark goes through
    /// <c>EffectManager.EnemyHitEffect(hitPos, moveDir, hitEnemy, EWeapon, weaponHitEffect, useSfx)</c>
    /// (0x1804FC150), which maps the weapon to a source string and calls the string overload
    /// (0x1804FBE40). That takes a pooled <c>AttackHit</c> from <c>PoolManager.GetProjectileHit</c>
    /// (0x18040A700), places it facing -moveDir and plays it. The pool is keyed by source; the prefab
    /// only seeds a new pool, and null seeds it with <c>powerupPrefab</c>. Callers, by xref:
    /// <c>WeaponAttack.ProjectileHit</c> (passes <c>prefabHit</c>), Dexecutioner (also
    /// <c>prefabHit</c>), and Aegis, Aura, Frostwalker, Firefield, DamageZone, laser, Axe and Bananarang
    /// pops, Black Hole, Dragon's Breath, Chunkers, all with null.
    /// <para>The host queues each call with <c>hitEnemy</c> set and flushes once per enemy tick as
    /// <see cref="EnemyHitEffects"/>; a client replays them with the same prefab argument, so the
    /// same pool. A client refuses its own enemy-hit sparks, which come from received copies on its
    /// own clock, except inside an attack its owner runs locally (Aegis, Space Noodle), which the host
    /// never runs for a client. Main thread only.</para>
    /// </summary>
    internal static class WeaponHitSparks
    {
        /// <summary>Entries per message: about 10 B each, so one batch stays under the MTU.</summary>
        private const int MAX_ENTRIES = 120;

        private static readonly INetTransport transport = Plugin.Services.GetService<INetTransport>();

        private static readonly EnemyHitEffects pending = new();
        private static readonly Dictionary<EWeapon, GameObject> prefabHits = new();

        private static bool replaying;
        private static int ownerRunDepth;

        private static int hostQueued;
        private static int drawn;
        private static int refusedLocal;
        private static int keptOwnerRun;
        private static float nextLogTime;

        internal static bool IsReplaying => replaying;

        /// <summary>Wraps an attack its owner runs on their own machine; see the class summary.</summary>
        internal static void EnterOwnerRun() => ownerRunDepth++;

        internal static void ExitOwnerRun()
        {
            if (ownerRunDepth > 0)
            {
                ownerRunDepth--;
            }
        }

        /// <summary>Client: true to let a local spark draw.</summary>
        internal static bool AllowLocal()
        {
            if (replaying)
            {
                return true;
            }

            if (ownerRunDepth > 0)
            {
                Bump(ref keptOwnerRun);
                return true;
            }

            Bump(ref refusedLocal);
            return false;
        }

        /// <summary>Host: queue one spark just drawn.</summary>
        internal static void Queue(Vector3 hitPos, Vector3 moveDir, EWeapon weapon, bool withWeaponPrefab, bool useSfx)
        {
            pending.Weapons.Add((short)weapon);
            pending.Flags.Add((byte)((withWeaponPrefab ? EnemyHitEffects.WEAPON_PREFAB : 0) | (useSfx ? EnemyHitEffects.USE_SFX : 0)));
            pending.Positions.Add(Quantizer.Quantize(hitPos.x));
            pending.Positions.Add(Quantizer.Quantize(hitPos.y));
            pending.Positions.Add(Quantizer.Quantize(hitPos.z));
            pending.Directions.Add(ToSByte(moveDir.x));
            pending.Directions.Add(ToSByte(moveDir.y));
            pending.Directions.Add(ToSByte(moveDir.z));
            Bump(ref hostQueued);

            if (pending.Weapons.Count >= MAX_ENTRIES)
            {
                Flush();
            }
        }

        /// <summary>Host: send what was queued. Once per enemy tick, and when a batch is full.</summary>
        internal static void Flush()
        {
            if (pending.Weapons.Count == 0)
            {
                return;
            }

            transport.SendToAllClients(pending, NetDelivery.ReliableUnordered);
            pending.Clear();
        }

        /// <summary>Client: draw the host's sparks.</summary>
        internal static void OnReceived(EnemyHitEffects effects)
        {
            var effectManager = EffectManager.Instance;
            if (effectManager == null || !effects.IsWellFormed())
            {
                return;
            }

            replaying = true;
            try
            {
                for (var i = 0; i < effects.Weapons.Count; i++)
                {
                    var weapon = (EWeapon)effects.Weapons[i];
                    var flags = effects.Flags[i];
                    var p = i * 3;
                    var hitPos = new Vector3(
                        Quantizer.Dequantize(effects.Positions[p]),
                        Quantizer.Dequantize(effects.Positions[p + 1]),
                        Quantizer.Dequantize(effects.Positions[p + 2]));
                    var moveDir = new Vector3(
                        effects.Directions[p] / 127f,
                        effects.Directions[p + 1] / 127f,
                        effects.Directions[p + 2] / 127f);
                    var prefab = (flags & EnemyHitEffects.WEAPON_PREFAB) != 0 ? GetPrefabHit(weapon) : null;

                    effectManager.EnemyHitEffect(hitPos, moveDir, true, weapon, prefab, (flags & EnemyHitEffects.USE_SFX) != 0);
                    Bump(ref drawn);
                }
            }
            finally
            {
                replaying = false;
            }
        }

        internal static void Reset()
        {
            if (hostQueued + drawn + refusedLocal + keptOwnerRun > 0)
            {
                Log();
            }

            pending.Clear();
            prefabHits.Clear();
            replaying = false;
            ownerRunDepth = 0;
            hostQueued = drawn = refusedLocal = keptOwnerRun = 0;
        }

        /// <summary>The weapon attack prefab's <c>prefabHit</c>, as <c>ProjectileHit</c> passes it.</summary>
        private static GameObject GetPrefabHit(EWeapon weapon)
        {
            if (prefabHits.TryGetValue(weapon, out var cached))
            {
                return cached;
            }

            GameObject prefab = null;
            var weapons = DataManager.Instance?.weapons;
            if (weapons != null && weapons.TryGetValue(weapon, out var data) && data != null && data.attack != null)
            {
                var attack = data.attack.GetComponent<WeaponAttack>();
                prefab = attack != null ? attack.prefabHit : null;
            }

            prefabHits[weapon] = prefab;
            return prefab;
        }

        private static sbyte ToSByte(float value) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(value * 127f), -127, 127);

        private static void Bump(ref int counter)
        {
            counter++;

            var now = Time.realtimeSinceStartup;
            if (now < nextLogTime)
            {
                return;
            }

            nextLogTime = now + 30f;
            Log();
        }

        private static void Log()
        {
            Plugin.Log.LogInfo(
                $"[hit-fx] session so far: host sent {hostQueued} weapon hit spark(s); drew {drawn} from the host, " +
                $"refused {refusedLocal} local, kept {keptOwnerRun} from own Aegis/laser.");
        }
    }
}
