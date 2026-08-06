# Megabonk Together — Fork Maintenance Documentation

Working documentation for `CEOYap/megabonk-together`, a fork of
[`Fcornaire/megabonk-together`](https://github.com/Fcornaire/megabonk-together).

These documents exist to answer three questions: what is actually broken in netplay right
now, what should be taken from the other forks, and what the transport layer should look
like next.

## Index

### Netplay

| Doc | What it covers |
|---|---|
| [`netplay/00-fork-comparison.md`](netplay/00-fork-comparison.md) | **Every other Megabonk multiplayer implementation** — the two related forks and the two independent mods, side by side: what each contributes, what to reject, and the replication-vs-determinism trade. Merges the former `08-delirium-comparison.md` |
| [`netplay/01-critical-fixes.md`](netplay/01-critical-fixes.md) | **Start here.** Ranked, implementable fix list with root cause, patch, and test for each |
| [`netplay/02-delivery-method-reference.md`](netplay/02-delivery-method-reference.md) | Reliability policy, the full current message→channel map, and how to classify a new message |
| [`netplay/03-cherry-pick-guide.md`](netplay/03-cherry-pick-guide.md) | Hunk-level guide to `Sea-Bass-cmd/optimized-netplay` — take / adapt / reject |
| [`netplay/04-performance-and-gc.md`](netplay/04-performance-and-gc.md) | Measured allocation sites, hot paths, and the ordered list of what to fix for 400–600 enemy density |
| [`netplay/05-local-testing.md`](netplay/05-local-testing.md) | Two netplay clients on one PC and one Steam account — and what that setup cannot prove |
| [`netplay/07-shared-experience-audit.md`](netplay/07-shared-experience-audit.md) | The shared-experience pause/reward barrier: protocol, its holes, and the upstream softlock issues |
| [`netplay/08-observed-bugs.md`](netplay/08-observed-bugs.md) | Backlog of bugs seen in play and not yet fixed: Aegis orbit count, Ghost item summons, and the two encounter-barrier symptoms from the 2026-08-06 session |
| [`netplay/09-performance-audit.md`](netplay/09-performance-audit.md) | Second pass on per-frame cost: the three globally patched Unity properties, the per-enemy interpolator Update, and what a profiler capture would settle |
| [`netplay/12-session-handover.md`](netplay/12-session-handover.md) | **Current branch state.** What is verified in-game, what is built but unplayed, the client spawn-ordering defect, and the lessons that cost time. Supersedes `10` and `11` |

### Transport

| Doc | What it covers |
|---|---|
| [`steamworks/00-migration-plan.md`](steamworks/00-migration-plan.md) | Phased plan to move from LiteNetLib + self-hosted rendezvous to Steamworks.NET |
| [`steamworks/01-api-mapping.md`](steamworks/01-api-mapping.md) | LiteNetLib → `ISteamNetworkingSockets` API and delivery-flag mapping reference |

### Reverse engineering

| Doc | What it covers |
|---|---|
| [`reverse-engineering/00-decompilation-guide.md`](reverse-engineering/00-decompilation-guide.md) | Toolchain and workflow for inspecting the IL2CPP game build |
| [`reverse-engineering/01-investigation-targets.md`](reverse-engineering/01-investigation-targets.md) | Concrete types and methods to decompile, each tied to an open question in the code |

### Reference

| Doc | What it covers |
|---|---|
| [`AUDIT_optimized-netplay.md`](AUDIT_optimized-netplay.md) | Full audit of `Sea-Bass-cmd/optimized-netplay` (source material for the docs above) |
| [`PROTON_SETUP.md`](PROTON_SETUP.md) | Running the mod on Linux via Proton |
| [`Setup-Own-Server.md`](Setup-Own-Server.md) | Self-hosting the rendezvous server |
| [`../NETPLAY_CHANGES.md`](../NETPLAY_CHANGES.md) | Player-facing description of gameplay changes the mod makes |

## Status legend

Used throughout these documents:

| Tag | Meaning |
|---|---|
| **CONFIRMED** | Verified by reading the code on `main` at the cited line. Reproducible by inspection. |
| **LIKELY** | Strong inference from code structure, but the failing path was not observed running. |
| **UNVERIFIED** | Depends on IL2CPP game internals not yet decompiled. Do not act on it without checking first. |

## Ground rules for this fork

1. **Most of these docs were written before anything was compiled or run**, by source analysis in
   an environment with no .NET SDK and no game install. Treat every code block as a proposal
   unless the surrounding text says it was played. **The exception is
   [`netplay/12-session-handover.md`](netplay/12-session-handover.md)**, which separates what is
   verified in-game from what merely builds — and that distinction is the point of the file.
   Where an older doc and `12` disagree, `12` was measured.
2. **Reliability is a correctness property, not a performance knob.** See
   [`02-delivery-method-reference.md`](netplay/02-delivery-method-reference.md) before
   changing any `DeliveryMethod`.
3. **Test under packet loss, not on LAN.** Most netplay bugs in this codebase are invisible
   at 0% loss. Use `clumsy` (Windows) or `tc netem` (Linux) at 2–5% before calling anything
   fixed.
4. **Wire-format changes need a version gate.** `MemoryPack` serializes positionally. Adding
   a field to any message in `src/common/Messages/` silently corrupts sessions between
   mismatched builds. See fix P1-3.

## Baseline

All line numbers in these documents refer to `main` at commit `041881b`
("chore: added some logs to identify a chest open issue"). Re-verify after rebasing.

```
041881b  chore: added some logs to identify a chest open issue    <- baseline
bd9518c  feat: more code optimizations
50b30a4  Merge pull request #92 from Fcornaire/chore/proton-finish  <- shared merge base with Sea-Bass fork
```
