using Assets.Scripts.Actors.Enemies;
using Assets.Scripts.Inventory__Items__Pickups.Weapons;
using Assets.Scripts.Objects.Pooling;
using OmniBonk.Common.Messages.GameNetworkMessages;
using OmniBonk.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>Wire values of <see cref="ItemVisualEntry.Kind"/>. Append only.</summary>
    internal enum ItemVisualKind : byte
    {
        Pooled = 1,
        HitSpark = 2,
        Zap = 3,
        MirrorFx = 4,
        PlayerFx = 5,
        RocketSpawn = 6,
        RocketDone = 7,
        Firefield = 8,
        BorgorSpawn = 9,
        BorgorEaten = 10,
        CampStart = 11,
        CampEnd = 12,
        LanternExplode = 13,
    }

    /// <summary>Wire values of <see cref="ItemVisualEntry.Sub"/> for <see cref="ItemVisualKind.Pooled"/>. Append only.</summary>
    internal enum ItemVisualPool : byte
    {
        None = 0,
        Bonk = 1,
        FreezeFx = 2,
        GrandmaTonic = 3,
        QuinMask = 4,
        SpicyMeatball = 5,
        Snek = 6,
        Poison = 7,
        Explosion = 8,
        Cactus = 9,
        CursedHit = 10,
    }

    /// <summary>Wire values of <see cref="ItemVisualEntry.Sub"/> for <see cref="ItemVisualKind.PlayerFx"/>. Append only.</summary>
    internal enum ItemPlayerFx : byte
    {
        GloveLightning = 1,
        GlovePoison = 2,
        GloveBlood = 3,
        GloveCurse = 4,
        GlovePower = 5,
        ElectricPlug = 6,
        ToxicBarrel = 7,
    }

    /// <summary>
    /// OB-62. Item visuals are drawn only on the machine that runs the item (host-run procs, owner-run
    /// reactions); this captures them there and replays them everywhere else. Decompiled call graph and
    /// per-item evidence: <c>docs/netplay/08-observed-bugs.md</c> OB-62.
    /// <list type="bullet">
    /// <item>Pooled one-shots: an item method opens a pool scope; <c>PoolManager.OnTakeFromPool</c>
    /// (0x18040ABF0), the get callback of every pool <c>CreateObjectPool</c>/<c>...Timeout</c> make,
    /// names the object taken; its transform is read when the scope closes. A receiver takes from the
    /// same pool and copies the transform.</item>
    /// <item>Item hit sparks: the string <c>EnemyHitEffect</c> (0x1804FBE40) outside a weapon call;
    /// every item caller passes a null prefab, so a replay with the source uses the same pool.</item>
    /// <item><c>ZapEffect</c>, <c>SpawnMirrorFx</c>: one item caller each; replayed as called.</item>
    /// <item>Player effects (gloves, Electric Plug, Toxic Barrel): parented to the owner, so a receiver
    /// keeps its own instance per owner and kind under that player.</item>
    /// <item>Slutty Cannon rockets: a damage-free copy that flies, and explodes where the sender's did.</item>
    /// </list>
    /// Main thread only.
    /// </summary>
    internal static class ItemVisualSync
    {
        private const float GLOVE_UP_OFFSET = 0.01f; // .rdata 0x18262EC1C

        private static readonly ISynchronizationService synchronizationService = Plugin.Services.GetService<ISynchronizationService>();
        private static readonly IPlayerManagerService playerManagerService = Plugin.Services.GetService<IPlayerManagerService>();
        private static readonly ITrackerService trackerService = Plugin.Services.GetService<ITrackerService>();
        private static readonly IEnemyManagerService enemyManagerService = Plugin.Services.GetService<IEnemyManagerService>();
        private static readonly INetTransport transport = Plugin.Services.GetService<INetTransport>();

        private static ItemVisualEffects pending = new();
        private static readonly Dictionary<string, ushort> sourceIndices = new(StringComparer.Ordinal);

        private static bool replaying;
        private static ItemVisualPool poolScope;
        private static int poolSuspend;
        private static readonly List<GameObject> taken = new();

        /// <summary>Replayed pooled objects, by instance id, so their damage paths stay off.</summary>
        private static readonly HashSet<int> replayedObjects = new();

        /// <summary>Replayed pooled objects per pool, oldest first, for recycling when a pool is full.</summary>
        private static readonly Dictionary<ItemVisualPool, Queue<GameObject>> replayedByPool = new();
        private static int recycled;

        private static readonly Dictionary<(uint Owner, ItemPlayerFx Fx), EffectPlayer> playerFx = new();

        private static uint nextRocketId;
        private static readonly Dictionary<int, uint> sentRockets = new();
        private static readonly Dictionary<(uint Sender, uint Id), Rocket> rocketCopies = new();
        private static readonly HashSet<int> rocketCopyIds = new();
        private static bool endingCopy;

        private static uint nextBorgorId;
        private static readonly Dictionary<int, uint> borgorIds = new();
        private static readonly Dictionary<uint, Borgar> borgors = new();
        private static readonly Dictionary<uint, Campfire> campfires = new();
        private static readonly Dictionary<uint, GameObject> lanterns = new();

        private static readonly int[] sent = new int[16];
        private static readonly int[] drawn = new int[16];
        private static readonly int[] missed = new int[16];
        private static float nextLogTime;

        internal static bool IsReplaying => replaying;

        internal static bool IsSessionActive => synchronizationService.HasNetplaySessionStarted();

        // ---------------------------------------------------------------- capture

        /// <summary>Opens a pool scope around an item method. Returns whether one was opened.</summary>
        internal static bool EnterPoolScope(ItemVisualPool pool)
        {
            if (replaying || poolScope != ItemVisualPool.None || !IsSessionActive)
            {
                return false;
            }

            poolScope = pool;
            taken.Clear();
            return true;
        }

        internal static void ExitPoolScope(bool opened)
        {
            if (!opened)
            {
                return;
            }

            var pool = poolScope;
            poolScope = ItemVisualPool.None;
            foreach (var obj in taken)
            {
                if (obj == null)
                {
                    continue;
                }

                var t = obj.transform;
                var entry = NewEntry(ItemVisualKind.Pooled, (byte)pool, t.position, t.rotation);
                var s = t.localScale;
                entry.Sx = s.x;
                entry.Sy = s.y;
                entry.Sz = s.z;
                Queue(entry);
            }

            taken.Clear();
        }

        /// <summary>Weapon and item hit effects take pooled objects too; they are not the item's.</summary>
        internal static void SuspendPool() => poolSuspend++;

        internal static void ResumePool()
        {
            if (poolSuspend > 0)
            {
                poolSuspend--;
            }
        }

        /// <summary><c>PoolManager.OnTakeFromPool</c>.</summary>
        internal static void OnTakeFromPool(GameObject obj)
        {
            if (poolScope != ItemVisualPool.None && poolSuspend == 0 && !replaying && obj != null)
            {
                taken.Add(obj);
            }
        }

        internal static void OnReturnedToPool(GameObject obj)
        {
            if (obj != null && replayedObjects.Count > 0)
            {
                replayedObjects.Remove(obj.GetInstanceID());
            }
        }

        internal static bool IsReplayedObject(GameObject obj) => obj != null && replayedObjects.Contains(obj.GetInstanceID());

        internal static void CaptureHitSpark(Vector3 hitPos, Vector3 moveDir, bool hitEnemy, string source, bool useSfx)
        {
            var entry = NewEntry(ItemVisualKind.HitSpark, (byte)((hitEnemy ? 1 : 0) | (useSfx ? 2 : 0)), hitPos, Quaternion.identity);
            SetDirection(ref entry, moveDir);
            entry.Source = SourceIndex(source);
            Queue(entry);
        }

        /// <summary>
        /// Client: a spark of its own Aegis or laser, which run only on their owner's machine, so the
        /// host never draws them for anyone. Sent as an item spark under the weapon's source string
        /// (<c>EffectManager.weaponNamesCache</c>, what the <c>EWeapon</c> overload looks up); both
        /// callers pass a null prefab.
        /// </summary>
        internal static void CaptureOwnerWeaponSpark(Vector3 hitPos, Vector3 moveDir, EWeapon weapon, bool useSfx)
        {
            if (!IsSessionActive)
            {
                return;
            }

            var names = EffectManager.weaponNamesCache;
            if (names != null && names.TryGetValue(weapon, out var source))
            {
                CaptureHitSpark(hitPos, moveDir, true, source, useSfx);
            }
        }

        internal static void CaptureZap(Vector3 pos) => Queue(NewEntry(ItemVisualKind.Zap, 0, pos, Quaternion.identity));

        internal static void CaptureMirror(Vector3 pos, Vector3 dir, float size)
        {
            var entry = NewEntry(ItemVisualKind.MirrorFx, 0, pos, Quaternion.identity);
            SetDirection(ref entry, dir);
            entry.Sx = size;
            Queue(entry);
        }

        /// <summary>A player-attached effect just played for <paramref name="owner"/>, at that local scale.</summary>
        internal static void CapturePlayerFx(ItemPlayerFx fx, Vector3 localScale)
        {
            var entry = NewEntry(ItemVisualKind.PlayerFx, (byte)fx, Vector3.zero, Quaternion.identity);
            entry.Sx = localScale.x;
            entry.Sy = localScale.y;
            entry.Sz = localScale.z;
            Queue(entry);
        }

        /// <summary>Host: a teammate's player effect was parented to the host's own player; move it to the teammate.</summary>
        internal static void ReparentToOwner(Transform effect)
        {
            var owner = CurrentOwner();
            var local = playerManagerService.GetLocalPlayer()?.ConnectionId;
            if (effect == null || owner == local)
            {
                return;
            }

            var model = playerManagerService.GetNetPlayerByNetplayId(owner)?.Model;
            if (model != null && effect.parent != model.transform)
            {
                var localPosition = effect.localPosition;
                effect.SetParent(model.transform, false);
                effect.localPosition = localPosition;
            }
        }

        /// <summary>An item rocket was just <c>Set</c> on this machine.</summary>
        internal static void CaptureRocketSpawn(Rocket rocket)
        {
            if (rocket == null || rocketCopyIds.Contains(rocket.GetInstanceID()))
            {
                return;
            }

            var id = ++nextRocketId;
            sentRockets[rocket.GetInstanceID()] = id;

            var t = rocket.transform;
            var entry = NewEntry(ItemVisualKind.RocketSpawn, 0, t.position, t.rotation);
            entry.Id = id;
            var target = rocket.targetEnemy;
            if (target != null)
            {
                var spawned = enemyManagerService.GetEnemyByReference(target);
                entry.Target = spawned.Value != null ? spawned.Key : 0;
            }

            Queue(entry);
        }

        /// <summary>An item rocket this machine sent is finishing (its <c>ProjectileDone</c>).</summary>
        internal static void CaptureRocketDone(Rocket rocket)
        {
            if (rocket == null || !sentRockets.Remove(rocket.GetInstanceID(), out var id))
            {
                return;
            }

            var entry = NewEntry(ItemVisualKind.RocketDone, 0, rocket.transform.position, Quaternion.identity);
            entry.Id = id;
            Queue(entry);
        }

        /// <summary>Dragonfire: a firefield just <c>Set</c> with no weapon.</summary>
        internal static void CaptureFirefield(Firefield field, float radius, float duration)
        {
            var entry = NewEntry(ItemVisualKind.Firefield, 0, field.transform.position, Quaternion.identity);
            entry.Sx = radius;
            entry.Sy = duration;
            Queue(entry);
        }

        /// <summary>Host: Borgor dropped a burger.</summary>
        internal static void CaptureBorgorSpawn(Borgar borgar, int flatHeal, float ratioHeal)
        {
            var id = ++nextBorgorId;
            borgorIds[borgar.GetInstanceID()] = id;
            borgors[id] = borgar;
            var entry = NewEntry(ItemVisualKind.BorgorSpawn, 0, borgar.transform.position, Quaternion.identity);
            entry.Id = id;
            entry.Sx = flatHeal;
            entry.Sy = ratioHeal;
            Queue(entry);
        }

        /// <summary>A burger, the host's or a copy, was eaten here: everyone else drops theirs.</summary>
        internal static void CaptureBorgorEaten(Borgar borgar)
        {
            if (borgar == null || !borgorIds.TryGetValue(borgar.GetInstanceID(), out var id))
            {
                return;
            }

            var entry = NewEntry(ItemVisualKind.BorgorEaten, 0, borgar.transform.position, Quaternion.identity);
            entry.Id = id;
            Queue(entry);
        }

        internal static void ForgetBorgor(Borgar borgar)
        {
            if (borgar != null && borgorIds.Remove(borgar.GetInstanceID(), out var id))
            {
                borgors.Remove(id);
            }
        }

        internal static void CaptureCampStart(Vector3 pos) => Queue(NewEntry(ItemVisualKind.CampStart, 0, pos, Quaternion.identity));

        internal static void CaptureCampEnd() => Queue(NewEntry(ItemVisualKind.CampEnd, 0, Vector3.zero, Quaternion.identity));

        internal static void CaptureLantern(GameObject explosion)
        {
            if (explosion == null)
            {
                return;
            }

            var t = explosion.transform;
            var entry = NewEntry(ItemVisualKind.LanternExplode, 0, t.position, t.rotation);
            var s = t.localScale;
            entry.Sx = s.x;
            entry.Sy = s.y;
            entry.Sz = s.z;
            Queue(entry);
        }

        internal static bool IsRocketCopy(Rocket rocket) => rocket != null && rocketCopyIds.Contains(rocket.GetInstanceID());

        /// <summary>A copy ends only when the sender's rocket does.</summary>
        internal static bool AllowCopyDone(Rocket rocket) => endingCopy || !IsRocketCopy(rocket);

        private static uint CurrentOwner()
        {
            var local = playerManagerService.GetLocalPlayer()?.ConnectionId ?? 0;
            return (synchronizationService.IsServerMode() ?? false) ? trackerService.GetCurrentPlayerId() ?? local : local;
        }

        private static ItemVisualEntry NewEntry(ItemVisualKind kind, byte sub, Vector3 pos, Quaternion rot) => new()
        {
            Kind = (byte)kind,
            Sub = sub,
            Owner = CurrentOwner(),
            Px = Quantizer.Quantize(pos.x),
            Py = Quantizer.Quantize(pos.y),
            Pz = Quantizer.Quantize(pos.z),
            Qx = ToSByte(rot.x),
            Qy = ToSByte(rot.y),
            Qz = ToSByte(rot.z),
            Qw = ToSByte(rot.w),
        };

        private static void SetDirection(ref ItemVisualEntry entry, Vector3 dir)
        {
            entry.Qx = ToSByte(dir.x);
            entry.Qy = ToSByte(dir.y);
            entry.Qz = ToSByte(dir.z);
        }

        private static ushort SourceIndex(string source)
        {
            source ??= string.Empty;
            if (!sourceIndices.TryGetValue(source, out var index))
            {
                index = (ushort)pending.Sources.Count;
                pending.Sources.Add(source);
                sourceIndices[source] = index;
            }

            return index;
        }

        private static void Queue(ItemVisualEntry entry)
        {
            pending.Entries.Add(entry);
            Bump(sent, entry.Kind);
        }

        /// <summary>Send what this frame queued. <c>NetworkHandler.Update</c>, once per frame.</summary>
        internal static void Flush()
        {
            if (pending.Entries.Count == 0)
            {
                return;
            }

            var batch = pending;
            pending = new ItemVisualEffects();
            sourceIndices.Clear();

            if (!IsSessionActive)
            {
                return;
            }

            batch.SenderId = playerManagerService.GetLocalPlayer()?.ConnectionId ?? 0;
            if (synchronizationService.IsServerMode() ?? false)
            {
                transport.SendToAllClients(batch, NetDelivery.ReliableUnordered);
            }
            else
            {
                transport.SendToHost(batch, NetDelivery.ReliableUnordered);
            }
        }

        // ---------------------------------------------------------------- replay

        internal static void OnReceived(ItemVisualEffects effects)
        {
            if (effects?.Entries == null || !IsSessionActive)
            {
                return;
            }

            replaying = true;
            try
            {
                foreach (var entry in effects.Entries)
                {
                    try
                    {
                        var ok = Replay(effects, entry);
                        if (ok)
                        {
                            Bump(drawn, entry.Kind);
                        }
                        else
                        {
                            Bump(missed, entry.Kind);
                        }
                    }
                    catch (Exception ex)
                    {
                        Bump(missed, entry.Kind);
                        Plugin.Log.LogWarning($"[item-vfx] Could not replay a {(ItemVisualKind)entry.Kind}: {ex.Message}");
                    }
                }
            }
            finally
            {
                replaying = false;
            }

            var isHost = synchronizationService.IsServerMode() ?? false;
            if (isHost && effects.SenderId != playerManagerService.GetLocalPlayer()?.ConnectionId)
            {
                transport.SendToAllClientsExcept(effects.SenderId, effects);
            }
        }

        private static bool Replay(ItemVisualEffects effects, ItemVisualEntry e)
        {
            var pos = new Vector3(Quantizer.Dequantize(e.Px), Quantizer.Dequantize(e.Py), Quantizer.Dequantize(e.Pz));

            switch ((ItemVisualKind)e.Kind)
            {
                case ItemVisualKind.Pooled:
                    {
                        var kind = (ItemVisualPool)e.Sub;
                        var pool = GetPool(kind);
                        var obj = TakeFromPool(kind, pool);
                        if (obj == null)
                        {
                            return false;
                        }

                        replayedObjects.Add(obj.GetInstanceID());
                        if (!replayedByPool.TryGetValue(kind, out var queue))
                        {
                            queue = new Queue<GameObject>();
                            replayedByPool[kind] = queue;
                        }

                        queue.Enqueue(obj);
                        obj.transform.SetPositionAndRotation(pos, ToRotation(e));
                        obj.transform.localScale = new Vector3(e.Sx, e.Sy, e.Sz);
                        return true;
                    }

                case ItemVisualKind.HitSpark:
                    {
                        var source = e.Source < effects.Sources.Count ? effects.Sources[e.Source] : null;
                        if (source == null || EffectManager.Instance == null)
                        {
                            return false;
                        }

                        EffectManager.Instance.EnemyHitEffect(pos, ToDirection(e), (e.Sub & 1) != 0, source, null, (e.Sub & 2) != 0);
                        return true;
                    }

                case ItemVisualKind.Zap:
                    if (EffectManager.Instance == null)
                    {
                        return false;
                    }

                    EffectManager.Instance.ZapEffect(pos);
                    return true;

                case ItemVisualKind.MirrorFx:
                    if (EffectManager.Instance == null)
                    {
                        return false;
                    }

                    EffectManager.Instance.SpawnMirrorFx(pos, ToDirection(e), e.Sx);
                    return true;

                case ItemVisualKind.PlayerFx:
                    return PlayPlayerFx(e);

                case ItemVisualKind.RocketSpawn:
                    return SpawnRocketCopy(effects.SenderId, e, pos);

                case ItemVisualKind.RocketDone:
                    return EndRocketCopy(effects.SenderId, e, pos);

                case ItemVisualKind.Firefield:
                    {
                        var obj = PoolManager.Instance?.firefieldPool?.Get();
                        var field = obj != null ? obj.GetComponent<Firefield>() : null;
                        if (field == null)
                        {
                            return false;
                        }

                        replayedObjects.Add(obj.GetInstanceID());
                        field.Set(pos, pos, e.Sx, e.Sy, 0f, null, string.Empty);
                        return true;
                    }

                case ItemVisualKind.BorgorSpawn:
                    {
                        var obj = PoolManager.Instance?.borgorPool?.Get();
                        var borgar = obj != null ? obj.GetComponent<Borgar>() : null;
                        if (borgar == null)
                        {
                            return false;
                        }

                        obj.transform.position = pos;
                        borgar.Set((int)e.Sx, e.Sy);
                        borgorIds[borgar.GetInstanceID()] = e.Id;
                        borgors[e.Id] = borgar;
                        return true;
                    }

                case ItemVisualKind.BorgorEaten:
                    {
                        if (!borgors.TryGetValue(e.Id, out var borgar) || borgar == null || !borgar.gameObject.activeSelf)
                        {
                            return false;
                        }

                        ForgetBorgor(borgar);
                        borgar.DestroyBorgar();
                        return true;
                    }

                case ItemVisualKind.CampStart:
                    {
                        if (!campfires.TryGetValue(e.Owner, out var camp) || camp == null)
                        {
                            var prefab = EffectManager.Instance?.campfire;
                            if (prefab == null)
                            {
                                return false;
                            }

                            camp = UnityEngine.Object.Instantiate(prefab).GetComponent<Campfire>();
                            if (camp == null)
                            {
                                return false;
                            }

                            campfires[e.Owner] = camp;
                        }

                        camp.StartFire(pos);
                        return true;
                    }

                case ItemVisualKind.CampEnd:
                    if (!campfires.TryGetValue(e.Owner, out var fire) || fire == null)
                    {
                        return false;
                    }

                    fire.EndFire();
                    return true;

                case ItemVisualKind.LanternExplode:
                    {
                        if (!lanterns.TryGetValue(e.Owner, out var lantern) || lantern == null)
                        {
                            var prefab = EffectManager.Instance?.lanternExplosion;
                            if (prefab == null)
                            {
                                return false;
                            }

                            lantern = UnityEngine.Object.Instantiate(prefab);

                            // The real one kills enemies (KillEnemyTrigger); a copy must not.
                            foreach (var trigger in lantern.RuntimeGetComponentsInChildren<KillEnemyTrigger>(true))
                            {
                                UnityEngine.Object.Destroy(trigger);
                            }

                            lanterns[e.Owner] = lantern;
                        }

                        lantern.transform.SetPositionAndRotation(pos, ToRotation(e));
                        lantern.transform.localScale = new Vector3(e.Sx, e.Sy, e.Sz);
                        lantern.SetActive(false);
                        lantern.SetActive(true);
                        return true;
                    }
            }

            return false;
        }

        private static bool PlayPlayerFx(ItemVisualEntry e)
        {
            var fx = (ItemPlayerFx)e.Sub;
            var parent = GetPlayerTransform(e.Owner);
            if (parent == null)
            {
                return false;
            }

            if (!playerFx.TryGetValue((e.Owner, fx), out var player) || player == null)
            {
                var prefab = GetPlayerFxPrefab(fx);
                if (prefab == null)
                {
                    return false;
                }

                var instance = UnityEngine.Object.Instantiate(prefab, parent);
                player = instance.GetComponent<EffectPlayer>();
                if (player == null)
                {
                    UnityEngine.Object.Destroy(instance);
                    return false;
                }

                instance.transform.localPosition = fx <= ItemPlayerFx.GlovePower ? Vector3.up * GLOVE_UP_OFFSET : Vector3.zero;
                playerFx[(e.Owner, fx)] = player;
            }

            player.transform.localScale = new Vector3(e.Sx, e.Sy, e.Sz);
            player.Play();
            return true;
        }

        private static Transform GetPlayerTransform(uint owner)
        {
            if (owner == playerManagerService.GetLocalPlayer()?.ConnectionId)
            {
                var local = GameManager.Instance?.player;
                return local != null ? local.transform : null;
            }

            var model = playerManagerService.GetNetPlayerByNetplayId(owner)?.Model;
            return model != null ? model.transform : null;
        }

        private static GameObject GetPlayerFxPrefab(ItemPlayerFx fx)
        {
            var em = EffectManager.Instance;
            if (em == null)
            {
                return null;
            }

            switch (fx)
            {
                case ItemPlayerFx.GloveLightning: return em.gloveLightning;
                case ItemPlayerFx.GlovePoison: return em.glovePoison;
                case ItemPlayerFx.GloveBlood: return em.gloveBlood;
                case ItemPlayerFx.GloveCurse: return em.gloveCurse;
                case ItemPlayerFx.GlovePower: return em.glovePower;
                case ItemPlayerFx.ElectricPlug: return em.electricPlugFx;
                case ItemPlayerFx.ToxicBarrel:
                    {
                        var player = GameManager.Instance?.player;
                        var found = player != null ? player.gameObject.RuntimeGetComponentsInChildren<ToxicBarrelEffect>(true) : null;
                        var barrel = found != null && found.Length > 0 ? found[0] : null;
                        return barrel != null && barrel.effectPlayer != null ? barrel.effectPlayer.gameObject : null;
                    }
            }

            return null;
        }

        private static bool SpawnRocketCopy(uint sender, ItemVisualEntry e, Vector3 pos)
        {
            var obj = PoolManager.Instance?.rocketPool?.Get();
            var rocket = obj != null ? obj.GetComponent<Rocket>() : null;
            if (rocket == null)
            {
                return false;
            }

            rocketCopyIds.Add(rocket.GetInstanceID());
            rocketCopies[(sender, e.Id)] = rocket;
            rocket.Set(pos, 0f, 0f, null, true, string.Empty);
            rocket.transform.rotation = ToRotation(e);

            if (e.Target != 0)
            {
                var target = enemyManagerService.GetEnemyById(e.Target);
                if (target != null)
                {
                    rocket.SetTarget(target);
                }
            }

            return true;
        }

        private static bool EndRocketCopy(uint sender, ItemVisualEntry e, Vector3 pos)
        {
            if (!rocketCopies.Remove((sender, e.Id), out var rocket) || rocket == null || !rocket.gameObject.activeSelf)
            {
                // The copy is gone: draw the explosion Rocket.ProjectileDone would have.
                var explosion = PoolManager.Instance?.explosionPool?.Get();
                if (explosion == null)
                {
                    return false;
                }

                explosion.transform.position = pos;
                return true;
            }

            rocketCopyIds.Remove(rocket.GetInstanceID());
            rocket.transform.position = pos;
            endingCopy = true;
            try
            {
                rocket.ProjectileDone();
            }
            finally
            {
                endingCopy = false;
            }

            return true;
        }

        /// <summary>
        /// Decompiled: <c>CreatePooledItem</c> (0x180409800) and <c>CreatePooledItemTimeout</c>
        /// (0x1804096E0) return null once a pool holds <c>maxSize</c> objects, and <c>ReturnToPool</c>
        /// (<c>OnEnable</c> 0x180522710, <c>Update</c> 0x180522820) times its release on <c>MyTime</c>,
        /// which stops while this player is in a reward screen. So replays could fill a pool the host's
        /// running clock keeps draining (488 of 685 missed in one hands-off run). A dead entry (destroyed
        /// while inactive) also comes back as null. When the pool gives nothing, the oldest copy replayed
        /// into it is released and the pool asked again.
        /// </summary>
        private static GameObject TakeFromPool(ItemVisualPool kind, UnityEngine.Pool.ObjectPool<GameObject> pool)
        {
            if (pool == null)
            {
                return null;
            }

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var obj = pool.Get();
                if (obj != null)
                {
                    return obj;
                }
            }

            if (!replayedByPool.TryGetValue(kind, out var queue))
            {
                return null;
            }

            while (queue.Count > 0)
            {
                var oldest = queue.Dequeue();
                // Only a copy still out: one that went back may since have been taken by this machine's own item.
                if (oldest == null || !oldest.activeSelf || !replayedObjects.Contains(oldest.GetInstanceID()))
                {
                    continue;
                }

                pool.Release(oldest);
                recycled++;
                return pool.Get();
            }

            return null;
        }

        private static UnityEngine.Pool.ObjectPool<GameObject> GetPool(ItemVisualPool pool)
        {
            var pm = PoolManager.Instance;
            if (pm == null)
            {
                return null;
            }

            switch (pool)
            {
                case ItemVisualPool.Bonk: return pm.bonkPool;
                case ItemVisualPool.FreezeFx: return pm.freezeFxPool;
                case ItemVisualPool.GrandmaTonic: return pm.grandmaTonicPool;
                case ItemVisualPool.QuinMask: return pm.quinMaskPool;
                case ItemVisualPool.SpicyMeatball: return pm.spicyMeatballPool;
                case ItemVisualPool.Snek: return pm.snekPool;
                case ItemVisualPool.Poison: return pm.poisonPool;
                case ItemVisualPool.Explosion: return pm.explosionPool;
                case ItemVisualPool.Cactus: return pm.cactusPool;
                case ItemVisualPool.CursedHit: return pm.cursedHitPool;
            }

            return null;
        }

        private static Quaternion ToRotation(ItemVisualEntry e)
        {
            var q = new Quaternion(e.Qx / 127f, e.Qy / 127f, e.Qz / 127f, e.Qw / 127f);
            var m = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return m > 0.001f ? new Quaternion(q.x / m, q.y / m, q.z / m, q.w / m) : Quaternion.identity;
        }

        private static Vector3 ToDirection(ItemVisualEntry e) => new(e.Qx / 127f, e.Qy / 127f, e.Qz / 127f);

        private static sbyte ToSByte(float value) => (sbyte)Mathf.Clamp(Mathf.RoundToInt(value * 127f), -127, 127);

        // ---------------------------------------------------------------- diagnostics

        internal static void Reset()
        {
            if (Sum(sent) + Sum(drawn) + Sum(missed) > 0)
            {
                Log();
            }

            pending = new ItemVisualEffects();
            sourceIndices.Clear();
            replaying = false;
            poolScope = ItemVisualPool.None;
            poolSuspend = 0;
            taken.Clear();
            replayedObjects.Clear();
            replayedByPool.Clear();
            recycled = 0;
            playerFx.Clear();
            sentRockets.Clear();
            rocketCopies.Clear();
            rocketCopyIds.Clear();
            nextRocketId = 0;
            nextBorgorId = 0;
            borgorIds.Clear();
            borgors.Clear();
            campfires.Clear();
            lanterns.Clear();
            Array.Clear(sent, 0, sent.Length);
            Array.Clear(drawn, 0, drawn.Length);
            Array.Clear(missed, 0, missed.Length);
        }

        private static string Describe(int[] counts)
        {
            var parts = new List<string>();
            for (var i = 0; i < counts.Length; i++)
            {
                if (counts[i] > 0)
                {
                    parts.Add($"{(ItemVisualKind)i} {counts[i]}");
                }
            }

            return parts.Count == 0 ? "none" : string.Join(", ", parts);
        }

        private static int Sum(int[] counts)
        {
            var total = 0;
            foreach (var c in counts)
            {
                total += c;
            }

            return total;
        }

        private static void Bump(int[] counts, byte kind)
        {
            if (kind < counts.Length)
            {
                counts[kind]++;
            }

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
                $"[item-vfx] session so far: sent pooled {sent[1]}, sparks {sent[2]}, zap {sent[3]}, mirror {sent[4]}, player {sent[5]}, " +
                $"rockets {sent[6]}/{sent[7]}; drew pooled {drawn[1]}, sparks {drawn[2]}, zap {drawn[3]}, mirror {drawn[4]}, " +
                $"player {drawn[5]}, rockets {drawn[6]}/{drawn[7]}; missed {Describe(missed)}, recycled {recycled}. " +
                $"Sent/drew firefield {sent[8]}/{drawn[8]}, borgor {sent[9]}/{drawn[9]} eaten {sent[10]}/{drawn[10]}, " +
                $"camp {sent[11]}/{drawn[11]} end {sent[12]}/{drawn[12]}, lantern {sent[13]}/{drawn[13]}.");
        }
    }
}
