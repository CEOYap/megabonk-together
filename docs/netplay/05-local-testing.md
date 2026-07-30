# Local Testing — Two Clients, One PC, One Steam Account

How to run a two-player netplay session on a single machine, for iterating on fixes without a
second person or a second Steam account.

---

## Why this works

**Netplay does not touch Steam.** `grep -rn "SteamUser\|CSteamID\|SteamAPI" src/plugin/Services/
src/plugin/Scripts/` returns nothing — identity is the mod's own `ConnectionId` plus the
`PlayerName` config value, and the transport is LiteNetLib UDP with a WebSocket matchmaker.
One Steam account is therefore not a constraint.

> **This changes if the Steamworks migration lands.** Once identity moves to `CSteamID`, two
> instances on one account will share an identity and this setup stops being representative.
> See [`../steamworks/00-migration-plan.md`](../steamworks/00-migration-plan.md).

**Ports resolve themselves.** `UdpClientService` starts at `27015` and increments until it finds
a free port (`UdpClientService.cs:134-153`), so the second instance takes `27016` with no
configuration. The log line `UDPClient listening on port N` confirms which it got.

---

## One-time setup

### 1. Second game copy

Steam refuses to launch the same appid twice, so the second instance runs from its own folder,
launched directly. The install is ~669 MB.

```powershell
$src = "C:\Program Files (x86)\Steam\steamapps\common\Megabonk"
$dst = "D:\MegabonkTest2"
Copy-Item $src $dst -Recurse
```

Both copies keep their own `BepInEx/config/MegabonkTogether.cfg`, which is the point — it is how
each instance gets its own `PlayerName`.

### 2. Local matchmaking server

```powershell
dotnet run --project src/server/MegabonkTogether.Server.csproj
```

Listens on `http://127.0.0.1:5000` (WebSocket matchmaking) and UDP `5678` (relay / NAT
introduction). Leave it running in its own terminal; its console output is useful — it logs
match creation and peer joins.

### 3. Point both instances at it

In **each** copy's `BepInEx/config/MegabonkTogether.cfg`:

```ini
[Network]
ServerUrl = ws://127.0.0.1:5000

[Player]
PlayerName = Host          # or "Client2" in the second copy — must differ, or the lobby is unreadable

[Updates]
CheckForUpdates = false    # stops the auto-updater replacing your test build
```

`CheckForUpdates = false` matters: `AutoUpdaterService` runs at startup and can overwrite the DLL
you are trying to test.

Note `ws://`, not `wss://` — there is no TLS on a local server.

---

## Each test run

### Deploy the build to both copies

`MegabonkPath` auto-copies Debug output to the **primary** install only. The second copy needs
syncing, so a build-and-deploy step looks like:

```powershell
dotnet build src/plugin/MegabonkTogether.Plugin.csproj -c Debug
Copy-Item "$env:MegabonkPath\BepInEx\plugins\MegabonkTogether\*" `
          "D:\MegabonkTest2\BepInEx\plugins\MegabonkTogether\" -Recurse -Force
```

Forgetting the second half means testing two different builds against each other, which produces
symptoms that look exactly like desync bugs. **If a result makes no sense, verify both copies
have the same DLL timestamp first.**

### Launch

Steam must be **running** (the game initialises Steamworks), but launch both instances by running
the executable directly rather than through the Steam UI:

```powershell
& "C:\Program Files (x86)\Steam\steamapps\common\Megabonk\Megabonk.exe"
& "D:\MegabonkTest2\Megabonk.exe"
```

`winhttp.dll` / Doorstop sits in each folder and loads with the process, so BepInEx injects
regardless of how the game was started.

### Connect

Use **Friendlies** (the private-code queue) rather than random matchmaking — it is deterministic
and does not depend on who else is online. One instance hosts and shows a code; the other joins
with it.

### Watch the logs

Each copy writes its own `BepInEx/LogOutput.log`. Tail both:

```powershell
Get-Content "C:\Program Files (x86)\Steam\steamapps\common\Megabonk\BepInEx\LogOutput.log" -Wait -Tail 20
Get-Content "D:\MegabonkTest2\BepInEx\LogOutput.log" -Wait -Tail 20
```

---

## What this setup cannot tell you

**Loopback has no packet loss, no latency, and no reordering.** Per
[`../README.md`](../README.md), most netcode bugs in this codebase are invisible at 0% loss —
delivery-method mistakes in particular are *silent* on localhost and only appear in the field.

A clean two-instance run on one PC proves a feature works. It does **not** prove the netcode is
correct. To get closer:

- **`clumsy`** (Windows) can drop and delay packets, but capturing loopback needs a filter that
  targets the mod's ports explicitly — e.g. `udp and (tcp.DstPort == 27015 or udp.DstPort ==
  27016)`. Verify it is actually intercepting before trusting a result; loopback capture is the
  part that most often silently does nothing.
- **Two physical machines on a LAN** with `clumsy` on one is more reliable than loopback
  shaping, and is the minimum for taking a "no desync" claim seriously.
- **Force the relay path.** Direct P2P on `127.0.0.1` always succeeds, so loopback never
  exercises the relay code that real IPv6 and failed-hole-punch users hit.

Also not covered: NAT traversal, the matchmaking server under concurrent load, and anything
involving more than two peers.

---

## Testing the Steam paths specifically

For [P0-0](01-critical-fixes.md#p0-0), the checks are:

| Check | How |
|---|---|
| No leaderboard entry from a netplay run | Finish a netplay run, then open the in-game leaderboard. The prefix blocks unconditionally, so this does **not** require clearing the game's own 349,999 score threshold |
| Achievements still unlock in netplay | Earn any achievement during a session; the Steam overlay popup is the signal |
| Singleplayer still uploads normally | Play a **non**-netplay run afterwards and confirm a leaderboard entry appears — this is the teardown-leak check, and it is the one most likely to regress |

The third row is the one worth caring about. A suppression that never turns back off looks fine
in every netplay test and silently breaks legitimate progression.
