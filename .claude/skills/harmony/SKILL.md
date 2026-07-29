---
name: harmony
description: |
  Harmony patching of Megabonk's IL2CPP Assembly-CSharp — patch shape, host/client gating, and the failure modes specific to patching a native runtime.
  Use when: adding or editing anything under src/plugin/Patches/, hooking a game method, deciding prefix vs postfix, or a patch fails to apply, applies twice, or leaks behaviour into singleplayer.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# Harmony Patches

~70 patched game types live in `src/plugin/Patches/`, mirroring Megabonk's own structure
(`Enemies/`, `Interactables/`, `Projectiles/`, `Items/`, `Player/`, `MapGeneration/`, `Unity/`).
Patches are applied by BepInEx from `Plugin.Load()`.

**Patches detect; services decide.** A patch's job is to notice that something happened locally
and hand it to a service in one call. Anything longer than ~20 lines belongs in `Services/`.

## The house pattern

```csharp
[HarmonyPatch(typeof(OpenChest))]
internal static class OpenChestPatches
{
    private static readonly Services.ISynchronizationService synchronizationService =
        Plugin.Services.GetService<Services.ISynchronizationService>();

    /// <summary>
    /// Send chest opened info to other players only when local player opens a chest.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(nameof(OpenChest.OnTriggerStay))]
    public static void OnTriggerStay_Postfix(Collider other, OpenChest __instance)
    {
        if (!synchronizationService.HasNetplaySessionStarted())
        {
            return;
        }

        if (__instance.readyForPickupTime <= MyTime.time
            && !__instance.pickedup
            && other == GameManager.Instance.player.GetComponent<Collider>()) // local player only
        {
            synchronizationService.OnChestOpened(__instance);
        }
    }
}
```

Four things every patch does, in order:

1. **Class-level `[HarmonyPatch(typeof(X))]`**, method-level `[HarmonyPatch(nameof(X.Method))]`.
2. **Bail out when not in netplay** — `synchronizationService.HasNetplaySessionStarted()`.
   Without this the mod changes singleplayer behaviour.
3. **Establish whose event this is** — local player? host? Sending on a remote-triggered event
   causes echo loops.
4. **One call into a service.**

## Prefix vs postfix

| Want | Use |
|---|---|
| Observe that it happened | `[HarmonyPostfix]` — the default here |
| Suppress the vanilla effect for remote players | `[HarmonyPrefix]` returning `bool` |
| Change the outcome | `[HarmonyPostfix]` with `ref __result` |
| Read state before the method mutates it | `[HarmonyPrefix]` (void, don't return false) |

Prefix returning `false` skips the original **and every later prefix**. Use it only for
deliberate suppression — e.g. stopping a client from running host-owned spawn logic — and
comment why.

```csharp
[HarmonyPrefix]
[HarmonyPatch(nameof(SomeSpawner.Spawn))]
public static bool Spawn_Prefix()
{
    // Host owns spawning; clients receive SpawnedEnemy instead.
    return synchronizationService.IsHost();
}
```

## Gating flags

`Plugin.cs` exposes static gates that patches read to decide whether vanilla logic may run:

```csharp
Plugin.CAN_SPAWN_PICKUPS
Plugin.CAN_SPAWN_CHESTS
Plugin.CAN_SEND_MESSAGES
Plugin.CAN_ENEMY_EXPLODE
Plugin.CAN_ENEMY_USE_SPECIAL_ATTACK
```

These exist so a *receiving* client can run the same game code path the host ran without
re-broadcasting it. When applying a remote message, the receiving service flips the relevant
flag, calls the game method, and flips it back. If you add a new synchronized mechanic that
needs this, follow the existing flag pattern rather than inventing a parallel one.

## IL2CPP-specific failure modes

- **Patching a stub does nothing visible.** The proxy in `stripped-libs/` has empty bodies but
  Harmony patches the *native* method at runtime. A patch that silently never fires usually
  means the signature didn't resolve — check the BepInEx log at startup for the patch-time
  exception; it does not surface later.
- **Overloads.** `nameof()` alone is ambiguous when the game overloads a method. Pass an
  explicit argument-type array: `[HarmonyPatch(nameof(X.Do), new[] { typeof(float) })]`.
- **Generic and interface methods** frequently don't resolve under IL2CPP. Patch the concrete
  implementing type.
- **`__instance` on a struct-like game type** needs `ref`.
- **Injected fields are marshalled.** Reading `__instance.someString` in a per-frame patch
  (`Update`, `OnTriggerStay`) costs a marshalled call every hit. Cache or early-out first — put
  the cheapest check first, as `OnTriggerStay_Postfix` does.

## Common mistakes

1. **No `HasNetplaySessionStarted()` guard** → the mod alters singleplayer. This is the #1
   regression source.
2. **No local-player / host check** → every peer rebroadcasts the same event; exponential echo.
3. **Logic in the patch** → move it to a service.
4. **Prefix `false` without a comment** → the next reader can't tell suppression from a bug.
5. **Per-frame patch doing work before the cheap guard** → measurable FPS cost across ~70
   patches.
6. **Assuming what the patched method does** from the proxy signature — verify against the dump
   (`docs/reverse-engineering/00-decompilation-guide.md`) and mark UNVERIFIED until you have.

## Related skills

- **il2cpp** — casting, collections, and why proxies have no bodies
- **netcode** — what to send once a patch has detected an event
- **csharp** — patch naming and the service boundary
