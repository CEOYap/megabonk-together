using System.Collections.Generic;
using UnityEngine;

namespace OmniBonk.Scripts.Snapshot
{
    public class TumbleWeedInterpolator : MonoBehaviour
    {
        private readonly Dictionary<uint, GameObject> activeTumbleWeeds = new Dictionary<uint, GameObject>();
        private readonly Dictionary<uint, List<TumbleWeedSnapshot>> snapshotsBuffers = new Dictionary<uint, List<TumbleWeedSnapshot>>();

        protected float interpolationDelayMs = 0.1f;
        protected int maxBufferSize = 200;

        private readonly List<uint> destroyedIds = new List<uint>();

        private bool loggedPrune = false;

        protected void Update()
        {
            double renderTime = Time.timeAsDouble - interpolationDelayMs;

            foreach (var kv in activeTumbleWeeds)
            {
                var tumbleWeedId = kv.Key;

                // OB-35: destroyed natively without a despawn message; GetComponent would throw.
                if (kv.Value == null)
                {
                    destroyedIds.Add(tumbleWeedId);
                    continue;
                }

                if (!snapshotsBuffers.TryGetValue(tumbleWeedId, out var buffer))
                    continue;

                if (!HasEnoughSnapshots(buffer))
                {
                    continue;
                }

                PerformInterpolation(tumbleWeedId, buffer, renderTime);
                CleanupOldSnapshots(buffer, renderTime);
            }

            if (destroyedIds.Count > 0)
            {
                foreach (var id in destroyedIds)
                {
                    activeTumbleWeeds.Remove(id);
                    snapshotsBuffers.Remove(id);
                }

                if (!loggedPrune)
                {
                    loggedPrune = true;
                    Plugin.Log.LogInfo($"[tumbleweed] OB-35: dropped {destroyedIds.Count} destroyed tumbleweed(s) from interpolation (logged once per level).");
                }

                destroyedIds.Clear();
            }
        }

        /// <summary>OB-35: on level change; tumbleweeds are scene objects and ids restart.</summary>
        public void Clear()
        {
            activeTumbleWeeds.Clear();
            snapshotsBuffers.Clear();
            loggedPrune = false;
        }

        public void UpdateTumbleWeeds(List<TumbleWeedSnapshot> tumbleWeedSnapshots)
        {
            if (tumbleWeedSnapshots == null || tumbleWeedSnapshots.Count == 0) return;

            foreach (var snapshot in tumbleWeedSnapshots)
            {
                AddSnapshot(snapshot);
            }
        }

        private void AddSnapshot(TumbleWeedSnapshot snapshot)
        {
            if (!snapshotsBuffers.TryGetValue(snapshot.Id, out var buffer))
            {
                buffer = new List<TumbleWeedSnapshot>();
                snapshotsBuffers[snapshot.Id] = buffer;
            }

            // Unreliable stream: drop out-of-order snapshots; the buffer must stay sorted.
            if (buffer.Count > 0 && snapshot.Timestamp <= buffer[buffer.Count - 1].Timestamp)
            {
                return;
            }

            buffer.Add(snapshot);

            if (buffer.Count > maxBufferSize)
            {
                buffer.RemoveAt(0);
            }
        }

        private bool HasEnoughSnapshots(List<TumbleWeedSnapshot> buffer)
        {
            return buffer.Count >= 2;
        }

        private void PerformInterpolation(uint tumbleWeedId, List<TumbleWeedSnapshot> buffer, double renderTime)
        {
            if (!activeTumbleWeeds.TryGetValue(tumbleWeedId, out var tumbleWeedObj))
                return;

            if (!FindSnapshotPair(buffer, renderTime, out TumbleWeedSnapshot older, out TumbleWeedSnapshot newer))
                return;

            float t = CalculateInterpolationFactor(renderTime, older.Timestamp, newer.Timestamp);
            t = Mathf.Clamp01(t);

            InterpolateSnapshot(tumbleWeedObj, older, newer, t);
        }

        private bool FindSnapshotPair(List<TumbleWeedSnapshot> buffer, double renderTime, out TumbleWeedSnapshot older, out TumbleWeedSnapshot newer)
        {
            older = null;
            newer = null;

            for (int i = 0; i < buffer.Count - 1; i++)
            {
                if (buffer[i].Timestamp <= renderTime &&
                    buffer[i + 1].Timestamp >= renderTime)
                {
                    older = buffer[i];
                    newer = buffer[i + 1];
                    return true;
                }
            }

            return false;
        }

        private float CalculateInterpolationFactor(double renderTime, double olderTime, double newerTime)
        {
            return (float)((renderTime - olderTime) / (newerTime - olderTime));
        }

        private void InterpolateSnapshot(GameObject tumbleWeedObj, TumbleWeedSnapshot older, TumbleWeedSnapshot newer, float t)
        {
            var interactable = tumbleWeedObj.GetComponent<InteractableTumbleWeed>();
            if (interactable == null || interactable.transform == null)
                return;

            interactable.transform.position = Vector3.Lerp(older.Position, newer.Position, t);
        }

        private void CleanupOldSnapshots(List<TumbleWeedSnapshot> buffer, double renderTime)
        {
            while (buffer.Count > 2 &&
                   buffer[0].Timestamp < renderTime - interpolationDelayMs)
            {
                buffer.RemoveAt(0);
            }
        }

        public void RegisterTumbleWeed(uint id, GameObject tumbleWeed)
        {
            activeTumbleWeeds[id] = tumbleWeed;

            if (!snapshotsBuffers.ContainsKey(id))
            {
                snapshotsBuffers[id] = new List<TumbleWeedSnapshot>();
            }
        }

        public void UnregisterTumbleWeed(uint id)
        {
            if (activeTumbleWeeds.TryGetValue(id, out var toDel))
            {
                if (toDel != null)
                {
                    DestroyImmediate(toDel);
                }
                activeTumbleWeeds.Remove(id);
            }

            snapshotsBuffers.Remove(id);
        }
    }
}
