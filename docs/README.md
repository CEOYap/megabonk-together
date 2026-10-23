# OmniBonk — Developer Documentation

Working documentation for OmniBonk. These documents answer what is broken in netplay right now,
how each fix was reasoned out, and what the transport layer looks like.

## Index

### Netplay

| Doc | What it covers |
|---|---|
| [`netplay/19-session-handover.md`](netplay/19-session-handover.md) | **Current state.** Release readiness for 1.0 and the record of the rename to OmniBonk |
| [`netplay/20-shrine-charge-scaling-plan.md`](netplay/20-shrine-charge-scaling-plan.md) | Plan, not built: charge shrines faster the more players stand in them |
| [`netplay/01-critical-fixes.md`](netplay/01-critical-fixes.md) | **Start here.** Ranked, implementable fix list with root cause, patch, and test for each |
| [`netplay/02-delivery-method-reference.md`](netplay/02-delivery-method-reference.md) | Reliability policy, the full current message→channel map, and how to classify a new message |
| [`netplay/04-performance-and-gc.md`](netplay/04-performance-and-gc.md) | Measured allocation sites, hot paths, and the ordered list of what to fix for 400–600 enemy density |
| [`netplay/05-local-testing.md`](netplay/05-local-testing.md) | Two netplay clients on one PC and one Steam account — and what that setup cannot prove |
| [`netplay/07-shared-experience-audit.md`](netplay/07-shared-experience-audit.md) | The shared-experience pause/reward barrier: protocol, its holes, and the reported softlock issues |
| [`netplay/08-observed-bugs.md`](netplay/08-observed-bugs.md) | Backlog of bugs seen in play and not yet fixed: Aegis orbit count, Ghost item summons, and the two encounter-barrier symptoms from the 2026-08-06 session |
| [`netplay/09-performance-audit.md`](netplay/09-performance-audit.md) | Second pass on per-frame cost: the three globally patched Unity properties, the per-enemy interpolator Update, and what a profiler capture would settle |
| [`netplay/15-session-handover.md`](netplay/15-session-handover.md) | Superseded by `19`. Three bug fixes (OB-11, OB-2, OB-9), none run in-game, and the one sitting that confirms them — including the conditions under which each would look like a no-op. Supersedes `14` |
| [`netplay/14-session-handover.md`](netplay/14-session-handover.md) | Phase 5: the netplay menu is deleted and the lobby panel is the UI. Superseded by `15`, kept for the Phase 5 history |
| [`netplay/13-session-handover.md`](netplay/13-session-handover.md) | Superseded by `14`. The bandwidth session: what is measured vs unplayed, the pre-Steamworks task list, and the counter that misattributed traffic for three sessions. Supersedes `12` |
| [`netplay/12-session-handover.md`](netplay/12-session-handover.md) | Superseded by `13`. What is verified in-game, what is built but unplayed, the client spawn-ordering defect, and the lessons that cost time. Supersedes `10` and `11` |

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
| [`reverse-engineering/02-item-visual-audit.md`](reverse-engineering/02-item-visual-audit.md) | Which items and weapons draw visuals on one machine only, the game chokepoints, and the replication design (OB-56) |

### Reference

| Doc | What it covers |
|---|---|
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

## Ground rules

1. **Most of these docs were written before anything was compiled or run**, by source analysis in
   an environment with no .NET SDK and no game install. Treat every code block as a proposal
   unless the surrounding text says it was played. **The session handovers** separate what is
   verified in-game from what merely builds — and that distinction is the point of them. Where an
   older doc and the latest handover ([`netplay/19-session-handover.md`](netplay/19-session-handover.md))
   disagree, the handover is later.
2. **Reliability is a correctness property, not a performance knob.** See
   [`02-delivery-method-reference.md`](netplay/02-delivery-method-reference.md) before
   changing any `DeliveryMethod`.
3. **Test under packet loss, not on LAN.** Most netplay bugs in this codebase are invisible
   at 0% loss. Use `clumsy` (Windows) or `tc netem` (Linux) at 2–5% before calling anything
   fixed.
4. **Wire-format changes need a version gate.** `MemoryPack` serializes positionally. Adding
   a field to any message in `src/common/Messages/` silently corrupts sessions between
   mismatched builds. See fix P1-3.

## Line numbers

Line numbers in older documents refer to the code as it was when they were written. Re-verify
against the current branch before acting on one.
