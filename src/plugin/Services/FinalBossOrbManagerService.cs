using OmniBonk.Common.Models;
using OmniBonk.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OmniBonk.Services
{
    public interface IFinalBossOrbManagerService
    {
        public void QueueNextTarget(uint targetId);
        public Tuple<uint, uint> PeakNextTarget();
        public Tuple<uint, uint> GetNextTargetAndOrbId();
        public void SetOrbTarget(uint targetId, GameObject target, uint orbId);

        public uint? RemoveOrbTarget(GameObject go);

        public bool ContainsOrbTarget(GameObject go);

        public void Reset();
        public IEnumerable<BossOrbModel> GetAllOrbs();
        public GameObject GetOrbById(uint id);
        public uint? GetTargetIdByReference(GameObject go);

        public void ClearQueueNextTarget();
    }

    internal class FinalBossOrbManagerService : IFinalBossOrbManagerService //TODO: Simplify this zzz
    {
        private class OrbInfo
        {
            public uint OrbId { get; set; }
            public uint TargetId { get; set; }
            public GameObject GameObject { get; set; }
        }

        private readonly ConcurrentDictionary<uint, OrbInfo> _orbsById = [];
        private readonly ConcurrentQueue<uint> _queuedTargetIds = [];
        private readonly ConcurrentQueue<(uint targetId, uint orbId)> _pendingOrbCreation = [];
        private uint _nextOrbId = 0;

        public void QueueNextTarget(uint targetId)
        {
            _queuedTargetIds.Enqueue(targetId);
        }

        public void ClearQueueNextTarget()
        {
            _queuedTargetIds.Clear();
        }

        /// <summary>Peeks the next orb target; repeatable before GetNextTargetAndOrbId.</summary>
        public Tuple<uint, uint> PeakNextTarget()
        {
            if (!_queuedTargetIds.TryPeek(out var targetId))
                return null;

            var orbId = ++_nextOrbId;
            _pendingOrbCreation.Enqueue((targetId, orbId));
            return Tuple.Create(targetId, orbId);
        }

        /// <summary>Takes the next orb target peeked by PeakNextTarget.</summary>
        public Tuple<uint, uint> GetNextTargetAndOrbId()
        {
            if (_pendingOrbCreation.TryDequeue(out var pending))
            {
                return Tuple.Create(pending.targetId, pending.orbId);
            }

            return null;
        }

        public void SetOrbTarget(uint targetId, GameObject target, uint orbId)
        {
            _orbsById[orbId] = new OrbInfo
            {
                OrbId = orbId,
                TargetId = targetId,
                GameObject = target
            };
        }

        public void Reset()
        {
            _orbsById.Clear();
            _queuedTargetIds.Clear();
            _pendingOrbCreation.Clear();
            _nextOrbId = 0;
        }

        public bool ContainsOrbTarget(GameObject go)
        {
            return _orbsById.Values.Any(orb => orb.GameObject == go);
        }

        public IEnumerable<BossOrbModel> GetAllOrbs()
        {
            return _orbsById.Values
                .Where(orb => orb.GameObject != null)
                .Select(orb => new BossOrbModel
                {
                    Id = orb.OrbId,
                    Position = Quantizer.Quantize(orb.GameObject.transform.position)
                });
        }

        public GameObject GetOrbById(uint id)
        {
            return _orbsById.TryGetValue(id, out var orb) ? orb.GameObject : null;
        }

        public uint? RemoveOrbTarget(GameObject go)
        {
            var orbToRemove = _orbsById.FirstOrDefault(kv => kv.Value.GameObject == go);

            if (orbToRemove.Value == null)
                return null;

            _orbsById.TryRemove(orbToRemove.Key, out _);
            return orbToRemove.Key;
        }

        public uint? GetTargetIdByReference(GameObject go)
        {
            var orb = _orbsById.Values.FirstOrDefault(o => o.GameObject == go);
            return orb?.TargetId;
        }
    }
}
