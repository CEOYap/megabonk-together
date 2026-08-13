# The lossy-link run: protocol

**Status: written, not yet run.** This is the procedure for Phase 4's last exit criterion — a full
co-op run on the Steam transport under 3% simulated packet loss — and for the reward-window desync
capture that has to happen in the same sitting.

Read [`06-next-session.md`](06-next-session.md) first for why this gates Phase 5: Phase 5 deletes
the transport you would otherwise fall back to.

## What this run can and cannot settle

The handover lists three weak points. **One of them is not testable this way, and tracing it is what
established that** — recorded here so nobody spends a run looking for it.

| Weak point | Settled by |
|---|---|
| `ReliableUnordered` degrades to reliable-ordered on Steam, so head-of-line blocking returns | **This run.** It is a loss-dependent effect and there is no other way to see it. |
| Unreliable sends above ~1200 B fragment unreliably | **This run**, indirectly — the promotion below is what prevents it, and the report will now say if it ever engaged. |
| The `MaxUnreliableBytes` promotion has never fired | **Argument, not this run.** See below. |

**Why the promotion is not on the list.** It keys off payload size, and packet loss does not change
payload size — no amount of loss can make it fire. Tracing what reaches it settles it anyway: the
per-entity streams are split to 1000 B by `StateBroadcastService.SendStreamUpdate` before they get
to the transport, and the only two unreliable sends that bypass the splitter are bounded by shape
(`EnemyDamaged` is all scalars; `PlayerUpdate` is ~800 B holding all 55 tomes and all 31 weapons at
once). Nothing can reach 1200 B. It is a backstop against a future oversized message, not a live
threshold.

It now logs when it fires, so the claim is at least falsifiable. **A run that never prints it is the
confirming result, not a missing one.**

## Setup

Two machines, two Steam accounts — [`../netplay/05-local-testing.md`](../netplay/05-local-testing.md)
explains why the two-instance-on-one-box harness cannot test anything Steam-dependent (the
direct-exe instance has no Steam, and both would share one identity).

On **both** machines:

| Where | Key | Value |
|---|---|---|
| `BepInEx/config/MegabonkTogether.cfg` | `[Network] UseSteamTransport` | `true` |
| same | `[Diagnostics] LogBandwidth` | `true` |
| `BepInEx/config/BepInEx.cfg` | `[Logging.Disk] WriteUnityLog` | `true` |

`UseSteamTransport` is read at startup and both peers must agree — a Steam host and a matchmaker
client cannot see each other at all. Confirm the `[log-capture]` **info** line at startup on both;
a warning there means that copy is not recording Unity lines and its silence proves nothing.

## The loss itself

`clumsy` on **one** machine only. Function **Drop**, chance **3%**, filter:

```
udp
```

Deliberately broad. WinDivert filters on packet headers and cannot select a process, and Steam
carries this over SDR relays whose ports are not fixed — so a narrow port filter is the most likely
way to run the whole test against traffic that was never touched. Take the collateral (Steam voice,
anything else on UDP) and use a machine that is not doing anything else.

`udp` alone hits both directions, so the link sees ~3% each way rather than 3% one way. That is the
harsher reading of the criterion and the right default. Use `outbound and udp` if you want a
strictly one-directional 3%.

### Check the rig before trusting the run — this step is not optional

Once connected and in a run, find a `[bw]` peer line in either log:

```
[bw]   peer 2 rtt 48 ms  quality 96.8%/99.9% (local/remote)  pending 0 B reliable, 0 B unreliable  unacked 0 B  queue 0.0 ms
```

**`quality` at or near 100.0% on both ends means clumsy is not filtering this traffic and the run
tested nothing.** Fix the filter and start over. Without this check a void run and a passing run
produce the same log, which is the failure mode that makes a clean result meaningless.

The clumsy end should show a depressed *local* quality; its peer sees it in *remote*. Loss is not
usually symmetric, which is why both numbers are printed.

## What to watch

**Head-of-line blocking.** The signature is `pending`/`unacked` bytes climbing on the reliable
channel while the unreliable streams keep flowing, with `queue` rising alongside. Steam has no
unordered-reliable channel, so every `ReliableUnordered` send is ordered whether it asked to be or
not, and one stalled message holds up every reliable message queued behind it. In play that reads as
spawns, chest opens and pickups arriving late and in a batch while movement stays smooth.

A number to write down rather than judge live: `unacked` should return to ~0 between events. A floor
that never comes back down is the interesting result.

**Anything that has to arrive exactly once.** Level transitions, the encounter barrier, readiness.
These are the paths where a single loss has historically been a permanent hang rather than a hiccup,
and 3% is where that gets exercised for the first time.

## Capturing both logs from the same session

The previous attempt at the reward-window desync failed on this: **the two logs available were from
different sessions**, so nothing in one could be lined up against the other.

`LogOutput.log` is overwritten at every launch. So, immediately when the run ends and **before
either machine relaunches the game**:

```powershell
Copy-Item "$env:ProgramFiles(x86)\Steam\steamapps\common\Megabonk\BepInEx\LogOutput.log" "$HOME\Desktop\run-2026-08-13-A-host.log"
```

Name the pair with the same run identifier and mark which end is the host and which had clumsy.
A pair that cannot be proven to be one session is worth nothing.

To confirm they *are* one session, check that the session id in the barrier lines matches across
both files. If it does not, the pair is unusable — do not analyse it.

## The reward-window desync

Confirm it still reproduces, then capture it. Charge shrine → reward window; the report is that the
window occasionally desyncs.

The barrier already logs enough to name the cause. Pull the lines from both files:

```powershell
Select-String -Path run-2026-08-13-A-host.log,run-2026-08-13-B-client.log -Pattern '\[barrier\]|stale barrier report'
```

Four lines matter, and which ones are present is the diagnosis:

| Line | Where | Means |
|---|---|---|
| `[barrier] Report from N accepted for round R; barrier closable: X` | host | a client's report was attributed to the open round |
| `Dropping a stale barrier report from N (session S, round R); host is on session S', round R'` | host | the ends disagree about which round is open — **the round numbers here are the diagnosis** |
| `[barrier] Released round R (session S) to all clients.` | host | the host released |
| `[barrier] Applied release for round R (session S).` | client | the client applied that release |
| `[barrier] Ignoring a stale release (session S, round R); this peer is on …` | client | a release arrived for a round this peer had already left |

A healthy round is one accepted report per client, one release, one applied line per client, and no
stale lines. Read the failure by which of those is missing:

- **Released, never applied** — the release was lost, or dropped as stale on the client. Under 3%
  loss this is the one to expect. `CloseEncounterStamped` is `ReliableOrdered`, so a plain loss
  should not do it; if it happens anyway, the round identity is what to suspect, not the delivery.
- **Report dropped as stale** — the client is reporting against a round the host has already moved
  past. Note both round numbers from the message; the gap size is the clue.
- **No barrier lines at all around the desync** — the window is desyncing on a path that is not the
  barrier, and the barrier is not the bug.

Do this capture whether or not the desync appears — a clean pair from a lossy run is itself a
result, and it is the pair that has been missing.

## What a pass looks like

A full run — lobby, character select, map, a complete run, at least one level transition — with:

- `quality` demonstrably below 100% for the duration (the rig was real),
- no permanent stall at any barrier, level transition or readiness gate,
- both logs captured, from the same session, with the session id matching.

Anything less than a complete run is not a pass. The criterion is a full run because the failures
being looked for are the ones that only appear at a transition.
