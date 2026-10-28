using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace OmniBonk.Helpers
{
    /// <summary>
    /// OB-66 check (Debug builds only). Once per weapon, describes the first received copy of its
    /// projectile: every component under it, and the first snapshots the host sends for it, to show
    /// whether the root (the transform <c>ProjectileInterpolator</c> rotates) spins between samples.
    /// Main thread only.
    /// </summary>
    internal static class ReceivedProjectileDiagnostics
    {
        private const int TRACED_SNAPSHOTS = 12;

        private static readonly HashSet<int> described = [];
        private static readonly Dictionary<uint, (int weapon, int left, StringBuilder text)> traced = [];

        [System.Diagnostics.Conditional("DEBUG")]
        internal static void Describe(int weapon, uint id, GameObject projectile)
        {
            if (projectile == null || !described.Add(weapon))
            {
                return;
            }

            // Never let a diagnostic break the spawn it is called from.
            try
            {
                var text = new StringBuilder();
                foreach (var component in projectile.RuntimeGetComponentsInChildren<UnityEngine.Component>(true))
                {
                    // The wrapper's own type is just Component; the runtime class names the real one.
                    var type = Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_name(
                        Il2CppInterop.Runtime.IL2CPP.il2cpp_object_get_class(((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)component).Pointer));
                    text.Append(text.Length == 0 ? "" : ", ")
                        .Append(component.transform == projectile.transform ? "" : component.gameObject.name + "/")
                        .Append(System.Runtime.InteropServices.Marshal.PtrToStringAnsi(type));
                }

                Plugin.Log.LogInfo($"[proj-diag] weapon {(EWeapon)weapon} received copy '{projectile.name}': {text}.");
                traced[id] = (weapon, TRACED_SNAPSHOTS, new StringBuilder());
            }
            catch (System.Exception ex)
            {
                Plugin.Log.LogWarning($"[proj-diag] could not describe weapon {weapon}: {ex.Message}");
            }
        }

        /// <summary>Angle of the host's forward vector from the previous snapshot, for a traced copy.</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        internal static void Snapshot(uint id, Vector3 position, Vector3 forward)
        {
            if (traced.Count == 0 || !traced.TryGetValue(id, out var trace))
            {
                return;
            }

            trace.text.Append(trace.text.Length == 0 ? "" : " | ")
                .Append($"pos {position.x:F1},{position.z:F1} fwd {forward.x:F2},{forward.y:F2},{forward.z:F2}");

            if (--trace.left > 0)
            {
                traced[id] = trace;
                return;
            }

            traced.Remove(id);
            Plugin.Log.LogInfo($"[proj-diag] weapon {(EWeapon)trace.weapon} first {TRACED_SNAPSHOTS} snapshots: {trace.text}.");
        }
    }
}
