# Shrine charge scaling — research and plan

**Status:** Not built. A port was written on 2026-10-21, compiled, then reverted on request before it
was committed. Nothing here has been run in-game. The decompile findings in §2 are **CONFIRMED**
against build 21750826. The design in §3 is a plan.

**Source:** [`caaatto/together-we-bonk@f4af058`](https://github.com/caaatto/together-we-bonk/commit/f4af058fb8fc0921a88f663c17e75f40ae6676a0),
"feat: charge shrines faster the more players stand in them". It is a fork of the same upstream as ours.
Their union tags and shrine code have diverged from ours, so this is a port, not a cherry-pick.

The feature: a charge shrine fills faster the more players stand in it. The required time is divided
by `1 + extraPlayers × factor`. At the default factor of 0.5, two players charge 1.5× as fast and
four players 2.5×. A factor of 0 keeps vanilla behaviour.

The game has nothing like this. `ChargeShrine` has no player count, unlike `BossLamp`'s `numPlayers`,
so every player in a shrine charges it at the single-player rate.

---

## 1. What the upstream commit does

| Change | Upstream | Ours |
|---|---|---|
| Track chargers as a real set (start/stop, local/host-receive) | Fixed in this commit | **Already done.** P0-1 / P2-2 in `HandleChargingStart` / `HandleChargingStop` and the shrine receive handlers. Nothing to port. |
| New message carrying the count | `ShrineChargersChanged`, union **70** | **Tag 70 is taken** (`CloseEncounterStamped`). Use the next free tag, 83 as of 2026-10-21. Re-check before building. |
| Speed factor config | `Gameplay.ShrineChargeSpeedPerExtraPlayer`, read by every peer | Read **on the host only**, multiplier sent in the message. See §3. |
| Apply the speed | Cache a base `currentChargeTime` once per shrine in `DynamicData`, then divide | Reset through the game's `FindChargeTime()` each time, then divide. See §2. |

---

## 2. What the game does (decompiled, CONFIRMED)

`ChargeShrine` layout from `dump.cs`:

| Field | Offset | Meaning |
|---|---|---|
| `chargeTime` | 0x58 | Base seconds to charge, from the prefab |
| `currentChargeTime` | 0x5C | Seconds to charge for this peer, after the Wrench bonus |
| `chargeProgress` | 0x60 | 0..1 |
| `charging` | 0x12D | bool |

Method VAs (buildid 21750826): `Update` 0x1804C2E20, `FindChargeTime` 0x1804C22A0,
`OnTriggerEnter` 0x1804C2640, `OnTriggerExit` 0x1804C2860.

Ghidra's applied field names are off by one slot here, as `CLAUDE.md` warns. It prints `field_0x4c`
for `currentChargeTime`, `chargeProgress` for `chargeTime`, and `zoneRenderer` for `chargeProgress`.
The offsets above are authoritative.

- **`Update`.** While `charging`, it runs `chargeProgress += dt / currentChargeTime`. Otherwise
  progress decays by `dt / (currentChargeTime × k)`. At 1.0 it calls `Complete()`. So
  `currentChargeTime` **is** the required time, and dividing it is the right lever. Upstream's
  reading of the field was correct.
- **`FindChargeTime`.** Sets `currentChargeTime` from `chargeTime`, reduced by the **local** player's
  Wrench (`ItemWrench.GetChargeSpeedIncrease`), with a floor. Interop exposes it, so it can be called.
- **`OnTriggerEnter`.** Opens with `if (rewardGiven || charging) return;`, then runs the same
  computation as `FindChargeTime`. Every fresh entry therefore resets `currentChargeTime` to base.
  A replay while the shrine is already charging is a no-op.
- **`OnTriggerExit`.** Does not touch `currentChargeTime`.

**Why not cache a base value as upstream does.** Their cache is taken the first time the shrine is
touched. That can be before any `OnTriggerEnter` has set the field. It also goes stale when the local
player picks up a Wrench mid-run. Calling `FindChargeTime()` and then dividing never compounds, and
it keeps the Wrench bonus current.

---

## 3. Design

### Message

```csharp
[MemoryPackable]
public partial class ShrineChargersChanged : IGameNetworkMessage
{
    public uint ShrineNetplayId { get; set; }
    public byte ChargerCount { get; set; }
    public float SpeedMultiplier { get; set; }
}
```

- **Host-sent only.** It goes in the client-side switch of `NetMessageRouter`, not the host-side one.
  It is republished through `EventManager` with a `Subscribe…` / `On…` pair.
- **The host computes `SpeedMultiplier`** from its own config: `1 + max(0, count − 1) × factor`.
  Upstream sends only the count, so peers with different `.cfg` values would charge at different
  rates.
- **`ReliableOrdered`.** A later message for the same shrine supersedes it, so in principle it could
  be sequenced. But it **must land after** the `StartingChargingShrine` it follows. That message's
  replayed `OnTriggerEnter` resets `currentChargeTime`, and would wipe a speed-up that arrived first.
  Same channel, sent second.
- Add a row to `02-delivery-method-reference.md` when built.

### Where the host broadcasts

Call one helper, `BroadcastShrineChargerCount(id)`, after every change to the shrine's set. The
helper returns at once on a client, and always runs **after** any `StartingChargingShrine` send.

1. `OnStartingToChargingShrine`, after `HandleChargingStart`.
2. `OnReceivedStartingToChargingShrine`, host branch. In the "already charging" path, after the
   replay send. In the first-charger path, after `OnTriggerEnter` and the send.
3. `OnStoppingChargingShrine`, after `HandleChargingStop`.
4. `OnReceivedStoppingChargingShrine`, host branch, after the remove.

The helper sends the message and applies it locally. Leave pylons and lamps alone: lamps already
scale natively through `numPlayers`.

### Apply

```csharp
shrine.FindChargeTime();
if (changed.SpeedMultiplier > 1f)
{
    shrine.currentChargeTime /= changed.SpeedMultiplier;
}
```

When the host itself is the first charger, its apply runs before vanilla `OnTriggerEnter`. That is
harmless: the count is 1 there, so the multiplier is 1 and both compute the same base.

The completion stays host-authoritative through OB-6 (`ChargeShrineCompleted`, tag 79). Each peer's
local bar uses its own Wrench bonus, the same as vanilla.

### Config

Put it in `Gameplay`, not `Lobby`. `Lobby` entries are backed by `LobbyPanel` buttons and
`LobbySettings`. The description should say "Only the host's setting counts", like `NoProjectileCap`.

---

## 4. Known gaps and open questions

- **Disconnect mid-charge (pre-existing).** Nothing removes a disconnected player from
  `shrineChargingPlayers`. Today that only leaves an extra entry in the set. With this feature, the
  shrine also stays sped up for the rest of the stage. Fix this first, or alongside.
- **Mixed versions.** A peer without the new union tag cannot deserialize it. This is the same as
  every message added since tag 70.
- **Balance.** 0.5 is upstream's starting value, not a tuned one. Boss lamps deliberately get
  *slower* with more players (`GameBalanceService`), and shrines would get *faster*. Decide whether
  that is wanted before shipping.
- **Testing.** Playtest at 2–5% packet loss. Check that a client who joins a shrine mid-charge
  speeds up, slows down when someone leaves, and that the speed-up survives a client running its
  replayed `OnTriggerEnter`.
