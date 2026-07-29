---
name: csharp
description: |
  MegabonkTogether code conventions: project layout, the DI service pattern, naming, nullability, and where each kind of code belongs.
  Use when: adding a service, patch, script, message or helper; deciding which of the three projects a file goes in; or reviewing whether new code matches existing style.
allowed-tools: Read, Edit, Write, Glob, Grep, Bash
---

# MegabonkTogether Conventions

Three projects, one solution (`MegabonkTogether.sln`), all `net6.0`:

| Project | Path | What it is | Can reference game types? |
|---|---|---|---|
| `MegabonkTogether.Common` | `src/common/` | Wire format — messages + models, MemoryPack only | **No.** Pure .NET. |
| `MegabonkTogether.Plugin` | `src/plugin/` | BepInEx IL2CPP plugin, `AssemblyName=MegabonkTogether` | Yes |
| `MegabonkTogether.Server` | `src/server/` | ASP.NET matchmaking + relay (`Sdk.Web`, Docker) | **No.** Never links Unity. |

Common is referenced by both ends, so **anything in `src/common/` must compile without Unity or IL2CPP**. A position in a message is `QuantizedVector3` (`src/common/Models/`), never `UnityEngine.Vector3`.

## Where code goes in the plugin

```
src/plugin/
├── Plugin.cs                  # BasePlugin entry, DI host, Il2Cpp type registration
├── Configuration/ModConfig.cs # every ConfigEntry<T>, static, bound once
├── Patches/                   # Harmony patches — one file per patched game type
├── Services/                  # I*Service + impl, registered in Plugin.cs, hold the logic
├── Scripts/                   # injected MonoBehaviours (NetworkHandler, interpolators, UI)
├── Helpers/                   # static stateless utilities (Quantizer, PoolHelper, DnsHelper)
└── Extensions/                # extension methods on game types (Enemy, MyPlayer, Pickup)
```

**Rule of thumb:** a patch decides *whether* something happened; a service decides *what to send and how to apply it*; a script runs per-frame. Patches stay thin.

## The DI service pattern

Every service is an interface + impl, registered as a singleton in `Plugin.Load()` and resolved through `Plugin.Services`.

```csharp
// src/plugin/Services/ChestManagerService.cs
public interface IChestManagerService
{
    void OnChestOpened(OpenChest chest);
}

internal class ChestManagerService : IChestManagerService { /* ... */ }
```

```csharp
// Plugin.cs — inside builder.ConfigureServices
services.AddSingleton<IChestManagerService, ChestManagerService>();
```

Resolving depends on where you are:

```csharp
// In a Harmony patch (static class) — cache in a static readonly field, resolved once.
private static readonly ISynchronizationService synchronizationService =
    Plugin.Services.GetService<Services.ISynchronizationService>();

// In an injected MonoBehaviour — resolve in Awake(), never in Update().
public void Awake()
{
    websocketClientService = Plugin.Services.GetRequiredService<IWebsocketClientService>();
}
```

Never call `Plugin.Services.GetService<T>()` inside `Update()` or any per-frame path — that is a dictionary lookup every frame.

## Cross-service communication: EventManager

`Services/EventManager.cs` is a static pub/sub hub with a `Subscribe*Events(Action<T>)` / `On*(T)` pair per event. Use it to decouple the network layer from gameplay code rather than having services hold references to each other.

```csharp
EventManager.SubscribeChestOpenedEvents(OnChestOpened);   // in Awake / ctor
EventManager.OnChestOpened(chestOpened);                  // from the receive path
```

## Naming and style

| Thing | Convention | Example |
|---|---|---|
| Namespace | `MegabonkTogether.<Folder>` | `MegabonkTogether.Services` |
| Class / interface / method | PascalCase, `I` prefix on interfaces | `IUdpClientService`, `HandleMatch` |
| Private field | camelCase, **no** underscore | `udpClientService`, `hasStarted` |
| Public static tunables & flags | SCREAMING_SNAKE_CASE | `CAN_SPAWN_CHESTS`, `PLAYER_FEET_OFFSET_Y` |
| Tick-rate constants | `<THING>_UPDATE_TICK_RATE` + derived interval + accumulator | see `Scripts/NetworkHandler.cs` |
| Patch class | `<GameType>Patches`, `internal static` | `OpenChestPatches` |
| Patch method | `<Method>_Postfix` / `_Prefix` | `OnTriggerStay_Postfix` |

The existing code uses no `_` prefix on fields. Match it; do not "fix" it.

## Language level

- `LangVersion=latest` in the plugin, `10.0` in the server. Implicit usings are **off** in the plugin, **on** in common/server.
- Collection expressions (`[]`) and primary constructors are already in use — `Dictionary<ECharacter, RawImage> CharactersIcon = [];`, `class DistanceThrottler(float mediumDistanceUpdateInterval = 1f)`. They're fine.
- `<Nullable>enable</Nullable>` is set in **common and server only**. The plugin has it off — don't add `?` annotations there expecting compiler enforcement; use `null!` and explicit null checks as the surrounding code does.
- `AllowUnsafeBlocks` is on in plugin and common (MemoryPack needs it).

## Common mistakes

1. **Putting a Unity type in `src/common/`** — breaks the server build. Quantize into a model instead.
2. **Business logic in a Harmony patch** — patches should detect and delegate to a service in one call.
3. **Resolving DI per frame** — cache in a static readonly field or in `Awake()`.
4. **New service not registered** — `GetRequiredService` throws in `Awake()`, which in IL2CPP surfaces as a MonoBehaviour that silently never ticks. Register it in `Plugin.cs`.
5. **Underscore-prefixed fields** — inconsistent with the whole codebase.

## Related skills

- **il2cpp** — before writing any code that touches a game type
- **netcode** — before adding a message or picking a `DeliveryMethod`
- **harmony** — before adding anything under `Patches/`
