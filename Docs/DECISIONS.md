# MLF Legends — Decision log

## Approved / fixed
- Repository: `HolyPenguin21/mlf-legends`, branch `main`.
- Owner's editor: **Unity 6000.5.4f1**. Use **Universal 3D (URP)** for 3D environment, not 2D template.
- Current work is **only combat**; do not implement or expand campaign design.
- Upgraded LF2/Nekketsu appearance: ~4-head-high expressive 2D sprites in a real 3D scene, 2.5D.
- Orthographic camera at a **fixed angle** over all zoom levels, plus panning and follow.
- ECS/DOTS + Burst-based simulation, specialized GPU sprite rendering, custom collisions and custom navigation. No full per-fighter Animator, Rigidbody or NavMeshAgent simulation.
- One authoritative combat system for a duel and full-scale **200 vs 200** battle. Human controls one fighter; others are AI.
- Attack facing mainly left/right with configurable Z-depth tolerance; attacks can hit multiple enemies.
- Moderate combat tempo, with windup/active/recovery, blocking and counters important.
- **Front-facing guard with stability**, guard break and **timely block advantage** without automated counter.
- Short directional defensive dash bound to **L (guard) + new direction press**, including depth and diagonals, with collision intact.
- All combatants collide. Allies yield somewhat; human fighter can press through allies with slight resistance. Enemies obstruct.
- Basic direct attacks don't hurt allies; skill-generated environmental/physical consequences can affect friend and enemy.
- Defeated fighters do not respawn before battle ends.
- **S.P.E.C.I.A.L.** and anatomical injuries (eyes, arms, legs, head/torso) with functional consequences.
- Prepared magic spells with several type-dependent resources, and environmental effects, postponed until later stages.
- Implementation roadmap 00–13, quality gates, maintainable clear ownership and systematic dependency checks.
- **Delivery policy (2026-10-25): Unity runtime/EditMode/PlayMode checks are NOT a blocking condition for commit, PR merge, push or continued implementation when the user cannot execute them.** Always run feasible static checks, mark unexecuted Unity checks **NOT RUN** in QA, and leave them for later verification. Never claim unexecuted tests passed; distinguish delivery from verified completion.

- **Stage 00 (2026-10-25):** owner confirmed all prepared Unity tests passed. Record **OWNER-REPORTED PASS**, without asserting the assistant independently reviewed run logs.
- **Agent management (2026-10-25):** assistant prepares GitHub Issues with technical scope, dependencies, acceptance criteria and PR review; Stage 01 is broken down into Issues #2–#7. Other agents work on separate branches, do not merge independently. Manager keeps ROADMAP and QA current.

## Visual-scene milestone (owner decision, 2026-10-25)

- Follow the existing roadmap in order: Stage 01 ECS, Stage 02 movement/camera, Stage 03 GPU sprite renderer.
- **No early temporary gameplay scene, capsule/proxy visual fighters or separate demo/vertical slice during Stage 02.** Camera and movement are implemented against the existing ECS authority and testable without an early display scene.
- Create the first **visible** scene only **after the sprite renderer is implemented and usable in Stage 03**, integrating the already-built movement and camera with actual sprite-backed combatants. This is not a request to defer all technical scenes: `CombatSmoke.unity` remains as the Stage 01 test/smoke scene.
- Do not create gameplay parallel paths solely to make the visual scene work. Stage 03 should display ECS state; combat attacks are not promised until Stage 05.

## Stage 01 completion and Stage 02 handoff (owner decision, 2026-10-25)

- Stage 01 performance Issue #7 **is integrated and manager-reviewed** (PR #24, commit `0ef2e24f904d7fdc0adb9e961bfd4efce114c2f9`); the six main Stage 01 tasks are delivered, **but Stage 02 must not start yet**. Stage 01 follow-up **#17** (Unicode Windows path/Burst) has **diagnostic-only PR #27 MERGED**, but its Windows repro remains **NOT RUN / root cause UNKNOWN**. To avoid unnecessarily idling independent work, deliver **one consolidated Bootstrap implementation PR #19 NEXT** (paused End/scene exit, disabled-Domain-Reload QA and bounded ECS debug-name pool, formerly Issue #26, closed as duplicate but not yet implemented), **while continuing #17 Windows evidence as a parallel requirement**. Conduct an **independent manager-led** Stage 01 audit **#23** only after both #19 and #17 have been resolved or the owner explicitly disposes of the unavailable Windows verification. No original test may be called PASS without being run; unresolved manual QA needs the owner's explicit disposition. #25 full-pending-command CPU cost is a measured conditional future scale risk, deferred until bulk input/AI producers or Stage 12, not a failed Stage 01 baseline.
- Only once Stage 01 closeout has been reviewed, the manager prepares **one full self-contained Russian context handoff for a new ChatGPT session**: repository, `main` HEAD, Unity/Entities/Burst/URP versions, ECS writer/owner contracts, integration and benchmark evidence, remaining later-stage Issues, first Stage 02 agent task, architectural constraints and PR workflow. Stage 02 will be planned and executed **in the new session**, not silently in the current thread.
- Input pacing Issue **#22** is a Stage 02 feature dependency, not pre-emptive Stage 01 implementation. Do not create an early playable visual scene in Stage 02; after Stage 03 GPU sprite renderer, assemble the first visible fight scene, keeping ECS authoritative.

## Battle restart and time policy (manager decision, 2026-10-25)

- **Do not recreate or reset the default Unity ECS World when restarting an individual fight.** A real World teardown is reserved for ending the application/game World, not for ordinary fight restart. World-scoped `SimulationClock` stays the single fixed-step authority and its unsigned `Tick` stays monotonic.
- **Battle session is not a second clock:** the battle bootstrap (#5) records an absolute `BattleStartTick` at a fixed-tick-safe boundary. Relative fight time is `SimulationClock.Tick - BattleStartTick`; the first simulated action belongs to relative tick 1. Replay tests (#6) record relative ticks and seed so the same sequence does not depend on World uptime.
- On restart, the battle lifecycle owner must stop/clear old actors and battle-only state, call the canonical command processor (#4) to clear queues/accepted history, then spawn new actors. No leftover future command, cached Entity handle or battle-local result may leak between fights.
- **Combatant IDs are monotonic and non-reusable throughout one World**, including repeated battles, allocated by one bootstrap owner (#5); this prevents late commands targeting a new fighter by reusing an ID. The allocator must reject exhaustion rather than wrap to `0`.
- Avoid implementing a second world, second clock, a session-time accumulator or duplicate command processor. Test repeated fights in the same World, plus separate-new-World isolation.
- If `SimulationClock` reaches `uint.MaxValue`, treating the World as exhausted and requiring a deliberate restart of the real World is an exceptional case, not a normal battle-restart mechanism.

## Deferred but previously discussed — campaign
- Strategic campaign is turn-based with months or years per turn.
- Individual character age, illness, injury, potential growth/decline, inheritance, childbirth and recruitment.
- Player controls one campaign character; after death player chooses ending campaign or continuing as a different character.
- No campaign work now; preserve these choices as background only.

## Open questions / values not yet approved
- Compatible Entities, Entities Graphics and other DOTS package releases for 6000.5.4f1, to be resolved within editor.
- Fixed combat tick and runtime performance budget/hardware.
- Attack/combo/heavy strike key binding and cancel windows.
- Guard stability formulas, timely block window, dash frames, reactions and body injury thresholds.
- Rendering method and asset format, equipment visual layers, terrain deformation implementation.
- GitHub Actions/Unity licensing and test runner configuration.

Any change to accepted decisions requires explicit conversation and an entry here.

## Pause-safe battle lifecycle (#19, implementation 2026-10-26)

- Schedule the same existing `BattleLifecycleSystem` in Initialization and before each fixed tick.
  Apply all request kinds at the next safe boundary, including paused frames; never invoke fake ticks.
- `BattleStartTick` records the last completed absolute tick at either boundary. A paused Start/Restart
  remains at relative tick 0 until the first real tick after resume (relative tick 1).
- Preserve the one request drain, owner-token checks, atomic preflight, ID allocator and queue reset path.
- Combatant ECB writes must play back at EndFixedStep in their producing tick. Next-tick BeginFixedStep
  playback can outlive paused-frame actor cleanup; no current runtime producer needs migration.
- Unity execution and Domain Reload GUI verification remain NOT RUN in this environment; #17's Windows
  gate and #23's independent manager review remain open. Delivery is not verified Stage 01 completion.
