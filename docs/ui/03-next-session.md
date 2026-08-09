# Handover: after the lobby panel

Written at the close of the branch that moved mod UI onto authored prefabs. The lobby panel is
done and verified with two players. This is what the next session needs and nothing it does not.

## Read these first, in this order

1. [`01-ui-asset-bundle.md`](01-ui-asset-bundle.md) — the prefab contract, the workflow, and the
   failure table. Every entry there is a trap that cost at least one build-and-launch cycle.
2. [`02-prefab-handover.md`](02-prefab-handover.md) — why the panel is a game `Window`, why
   buttons are clones, and the list of things not to "clean up".
3. [`00-lobby-panel.md`](00-lobby-panel.md) — what the panel is for and which union tags it added.

## State

Branch `claude/lobby-panel-ui`, 38 commits ahead of `main`, builds clean, PR raised.

Verified in a two-player run with zero `Error :MegabonkTogether` lines: bundle load, prefab
instantiate and bind, buttons with the game's own art, Start greyed until everyone is ready and
hidden entirely on clients, the local row synthesized before the peer list arrives, real roster
rows once a peer joins, the readiness broadcast, and the host-gated Start advancing every peer to
character selection.

## Open, small

- **The member row's READY column has never been seen.** The log proves readiness state is set;
  no screenshot has caught a row while ready. Check the right-hand column turns green.
- **The prefab is scaffold styling** — correct structure, placeholder colours. Purely editor
  work now: open `unity-ui/`, style, run **Build UI Bundle**, rebuild the plugin. No launch cycle
  needed to see layout.
- **Controller navigation is untested.** The panel is a real `Window` with its buttons in
  `allButtons`, so it should work, but nobody has tried a pad.
- **Escape may now close the panel** via the game's own `Window.Close()`. Untested, and probably
  desirable.

## The next phase: Steamworks Phase 2

Phase 1 landed earlier on this branch's ancestry — the `INetTransport` seam and `NetDelivery`
exist, and `IUdpClientService` is registered as the transport behind them. Phase 2 is the Steam
implementation behind that seam.

Design notes live in `docs/steamworks/`. Standing rules for that work:

- Reference [Steamworks.NET](https://github.com/rlabrecque/Steamworks.NET) and the
  [official Steamworks docs](https://steamworks.github.io/) directly. Do not name any other mod
  in commit messages or design docs.
- Union tags are append-only. Tags 73/74 (`LobbyReadyChanged`, `LobbyReadyState`) become
  deletable once Steam lobby member data replaces them; tag 75 (`LobbyStartRequested`) survives.
- Two players is the testing maximum. Do not design anything needing more.

Note `SteamAPI_Init() failed` appears in every log — the game's own Steamworks, not ours. It will
matter in Phase 2; it does not now.

## Five lessons this branch paid for

**Verify the artefact before debugging the reader.** Four consecutive fixes to bundle *loading*
were chasing a file that was never loadable, because `Packages/manifest.json` was missing
`com.unity.modules.assetbundle`. `BuildAssetBundles` reported success and wrote a valid-looking
header. `MegabonkTogether/Verify UI Bundle` answers this in one headless run — use it first.

**Do not drop lines from a working design because they look incidental.** Three times: the
`Il2CppStructArray` field, the `MemoryStream` wrapper, and `SetPersistentListenerState`. Each was
load-bearing; each cost a cycle to rediscover.

**Signatures do not tell you what marshals.** `LoadAsset` and `LoadAssetAsync` have identical
`_Injected` native forms; one works here and one does not. Reading the signature list produced a
confident wrong conclusion. Only running it settled it.

**"Builds clean" means very little.** Roughly a dozen changes on this branch compiled and failed
on first load. State what is unverified, every time.

**The `unity-libs` / `interop` split is the recurring hazard.** `UnityEngine.Object` has no
`TryCast`, `UnityAction` is a managed delegate with no bridge, `AsyncOperation` hides its internal
fields. Each was worked around individually — reflection, field copying, polling. If it blocks a
fifth thing, migrating `UnityEngine.CoreModule` to `interop` is the structural fix, and it is a
deliberate piece of work rather than a quick swap.

## Standing constraints

- Nothing is verified until run in-game. There is no test suite.
- One logical change per commit, with what is unverified stated in the body.
- MemoryPack union tags are append-only — never renumber, reuse or remove.
- Do not name the reference mod or its author in commits, titles or docs.
