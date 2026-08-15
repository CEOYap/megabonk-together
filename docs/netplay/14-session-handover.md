# Handover — Phase 5, and the lobby panel becomes the UI

Supersedes [`13-session-handover.md`](13-session-handover.md). Read this first, then
[`../steamworks/06-next-session.md`](../steamworks/06-next-session.md) for what Phase 4 left open
and [`06-session-handoff.md`](06-session-handoff.md) for the standing queue.

Branch: `claude/phase5-loss-test-and-session-service`, 37 commits off `main`.

**Phase 5 is done: `NetworkMenuTab` is deleted and TOGETHER! opens the lobby panel directly.** Along
the way the first same-session two-machine log pair this project has had settled the lossy-link run,
the mod's largest source of exceptions was fixed and confirmed, and OB-12 was fixed after its
recorded cause turned out to be attributed to the wrong class.

**If you read two things:** *Start here next session*, and *What is verified and what is not* — the
second is short and it is the thing most likely to bite.

---

## Start here next session

**One playtest unblocks almost everything below.** Four separate items need the same two-machine
setup, so do them in one sitting:

1. **Loss-test run 2.** The only unmet Phase 4 exit criterion. Run 1 passed at ~40% loss but the
   level transition never happened *under* loss — clumsy went on after arriving at level 2 and the
   session ended there. **Start clumsy in the lobby and cross at least one transition.** The
   readiness barrier is the path the Phase 4 branch records as having broken four times, and it is
   the one thing a lossy run has still never exercised. Procedure:
   [`../steamworks/07-lossy-link-playtest.md`](../steamworks/07-lossy-link-playtest.md).
2. **A two-player pass on Phase 5 steps 3–5.** Join Code, Stop on the connecting window, the Options
   sub-view round trip, and the invite path. None has run with a second machine, and step 5 removed
   the fallback.
3. **OB-12's fix.** The Shrine Counter panel should now *agree* between peers after each has used
   interactables the other did not. Previously the giveaway was large arbitrary gaps.
4. **The client's exception count.** One grep, settles a hypothesis — see *What is verified* below.

Then, not needing a playtest:

5. **OB-11**, the only known bug left with no fix. It wants one decompile before any code — see
   below.
6. **The rename to OmniBonk.** Deliberately deferred to "once everything is done". Notes below.

---

## What landed

### Phase 5 — the netplay menu is gone

All five steps of [`../ui/05-drop-the-netplay-menu.md`](../ui/05-drop-the-netplay-menu.md), which is
now marked done and carries the reasoning for each. Summary:

| Step | What changed |
|---|---|
| 1 | Matchmaker paths moved onto `INetplaySessionService`; `HandleFriendlies` and `HandleConnectionStatus` deleted. `NetworkHandler.HandleNetworking()` has exactly one caller. |
| 2 | TOGETHER! opens the lobby panel and hosts. `SteamTicker`'s invite path consumes the code and joins. |
| 3 | Join Code works — `OnJoinRequested` had never been assigned. Connecting window with Stop. Random retired. |
| 4 | Netplay Options as a panel sub-view, replacing the member list and column. |
| 5 | `NetworkMenuTab` deleted — 1,242 lines. |

**Two deletions the plan called for were not made, deliberately**, both recorded in that doc:
`ModConfig.PlayerName` (it is storage, not UI — twelve readers, and `SteamPersonaService` *writes*
to it) and `NetworkModeType.Random` (`WebsocketClientService` still switches on it to reach
`ConnectRandomAsync` and throws on an unknown mode). The second goes when the matchmaker transport
does.

### The lobby panel is now the netplay UI

- **Resized** 620x950 → 500x785 after it ran off the bottom of the screen. Two independent causes:
  the card was 88% of a 1080-unit reference, *and* the canvas matched width and height equally, so a
  21:9 window scaled it **up** and made a wide screen worse than a 16:9 one. It matches height alone
  now. Both halves are recorded in `ScaffoldLobbyPanel`.
- **Steam avatars** in the member rows, resolved without a wire change — see below.
- Seven buttons fit in the column with six units spare. **There is no room for an eighth**; anything
  new is a sub-view or replaces something.

### Steam avatars, without touching the protocol

The interesting part was the identity join: the roster keys everyone by `ConnectionId`, Steam keys
everyone by `CSteamID`, and a row needs both. The obvious route — a `SteamId` on the `Player` record
— is a protocol break, and `Protocol.Version` is already 2 with v1 and v2 lobbies refusing each
other.

Instead each peer publishes its connection id into **Steam lobby member data** (`mt_cid`) and every
peer builds the map locally. Nothing new on the wire, and it works for all six players — the
transport's own peer table would not have, because a client holds a connection to the host and
nobody else.

### Two fixes, one confirmed in game

**The custom-button null-background bug is fixed and confirmed.** Every mod-made button discarded
eight serialized fields when its `MyButtonNormal` was swapped out, and `SetColor` dereferenced one
of them on every hover. `LobbyPanel` had the only correct swap; its body is now
`Helpers/ButtonStyle` and all six sites use it. **1716-and-7 → 0** on the host, in a session that
clicked more than the one that produced the 7.

**OB-12 is fixed, unverified.** Counter identified as `InteractablesStatus`; both shortcut branches
now call `InteractablesStatus.OnInteractableUse` before taking the shortcut.

---

## What is verified and what is not

Short, and worth reading before trusting anything above.

| Verified on screen or in a log | Not verified — needs two machines |
|---|---|
| Panel opens from TOGETHER!, hosts, sizes at 16:9 and 21:9 | Join Code |
| Steam avatars render | Stop on the connecting window |
| Button column measures 3 buttons at 176 of 430 units | Options sub-view round trip |
| Button NRE storm: 0 on the host | The invite path |
| Lossy run 1: barrier 86/86, zero send failures at ~40% loss | A level transition under loss |
| | OB-12's fix |
| | The client's exception count after the button fix |

**One hypothesis still open.** The client's 1716 exceptions were never *traced* — only shown to be
consistent with the button bug (252 bursts averaging 6.8, the shape `StartedHoveringButton` makes
across several mod buttons). No client has been counted since the fix. One grep closes it:

```bash
grep -c '\[Error  :     Unity\] NullReferenceException' LogOutput.log
```

---

## OB-11 — the one bug left, and it needs a decompile first

[OB-11](08-observed-bugs.md#ob-11): minibosses spawn near the host. **Status LIKELY, and the gap is
specific** — it is confirmed that the mod supplies no spawn position and replicates the host's, and
*not* confirmed that the game's spawner derives its position from `GameManager.Instance.player`. No
spawn-position method has been decompiled.

**Do that decompile before choosing anything.** The entry lists three fix shapes in increasing cost,
and which is right depends on the answer. It also records one thing not to do: spawning
independently on each peer produces two minibosses with different ids, which is the desync the
architecture exists to prevent.

Worth knowing given how the last two bugs went: **both OB-12 and the button bug had a recorded cause
that was wrong in a way that mattered**, and in both cases the correction came from reading the game
rather than the entry. OB-11 is explicitly marked LIKELY rather than CONFIRMED, so treat its
reasoning as a hypothesis with the decompile as the test.

---

## The rename to OmniBonk

Deferred by decision to "once everything is done and no more bugs", with versioning restarting from
the beginning. Two things to know when it happens:

- **It removes a problem rather than adding one.** The release trio was blocked on
  `Protocol.Version 2` breaking v1 lobbies with no in-game explanation. Restarting under a new name
  means there is no installed base to keep compatible, and `Protocol.Version` can reset to 1 — the
  whole v1/v2 refusal story leaves the docs rather than being carried forward.
- **It is not a find-and-replace.** Assembly name, `BepInPlugin` GUID, the config file name
  (renaming `MegabonkTogether.cfg` silently resets everyone's settings), the embedded AssetBundle
  resource name, and the Thunderstore package identity. Worth its own branch and a checklist.

The release trio (`CHANGELOG.toml`, `README.md`, csproj `<Version>`) is therefore **not** outstanding
work any more — it is superseded.

---

## Five things this branch cost, worth not repeating

**1. `as` and `is` on an Il2CppInterop proxy test the managed wrapper, not the object.** This bit
three times in two files. `buttonContainer as RectTransform` returned null for something that is a
`RectTransform`, and fixing it left `child is not RectTransform` two lines below doing the same
thing — so the diagnostic still printed nothing and the log looked unchanged. Use
`GetComponent<T>()`. `Transform.Find` and `GetChild` both hand back `Transform`-typed wrappers.

**2. A diagnostic that cannot run is worse than none.** `WarnIfButtonColumnOverflows` had been in
place through two column overflows and reported neither; both were found by screenshotting the game,
which is exactly what it existed to prevent, and its silence was read as "the column fits" each
time. When it finally ran it reported a *false* overflow, because `Destroy` is deferred and five
destroyed placeholders were still being counted in the same frame.

**3. The Unity reference split is a runtime trap, not a compile one.** `Assembly-CSharp` and
Steamworks come from `BepInEx/interop` (Il2CppInterop proxies); `UnityEngine.CoreModule` comes from
`unity-libs` (plain managed Unity). Any Unity method taking an **array** is declared `T[]` at compile
time and `Il2CppStructArray<T>` at run time — it links, then throws `MissingMethodException` on first
call. `SetPixels32` did exactly that. Route pixels through `LoadRawTextureData(IntPtr, int)` and
prefer `RawImage` over `Sprite.Create`.

**4. Ask where a counter is on screen before decompiling what might produce it.** OB-12's counter was
identified from a UnityExplorer object path in one sentence, after three dump findings had only
succeeded in ruling `RunStats` out.

**5. A `try` cannot protect a body that will not compile.** `MissingMethodException` is raised when
the containing method is *JIT-compiled*, not when the missing call runs, so a `try` inside that
method never executes. It has to go one level up, at the call site. `Plugin.Load` already carried
this note; `CountInteractableAsUsed` now does too.

---

## Standing constraints, unchanged

- Two players is the testing maximum.
- Nothing is verified until run in-game. There is no test suite.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only.
- Do not name another mod or its author in commits, titles or docs.
