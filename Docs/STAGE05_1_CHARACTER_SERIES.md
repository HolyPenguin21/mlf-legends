# Stage 05.1 / #69 — physics fixes, per-character data, U/I series and O ability

Base: fresh main `86012a006b51f6295a855dbaa9b6bbf1d3d23133` (merge of PR #68 / #66). Branch
`feature/69-stage05-character-series`, one PR to main. **Merge only on a separate owner command.** Stage 03 OWNER-ACCEPTED;
Stage 04 manual acceptance still open; Stage 05 IN PROGRESS. This report does not close Stage 04 or 05.
Commit order: confirmed fixes → per-character data → input/series → checks → docs/evidence.
Exact tested SHA and PASS/FAIL/NOT RUN: [Evidence/69](Evidence/69/README.md).

Not in this delivery (05.2+): self impulse / flight, dynamic Height, swept hits along the path, contact attenuation by
stats, VFX, the new atlas (#70 / Stage 05.1a), block/dash (Stage 06), magic (Stage 13).

## 1. Base fixes (first commits)

### 1.1 Final velocity after contact (`CombatBodyContacts`, `CombatArenaGeometry`)

Confirmed defect: `velocity = Physical / dt` published the tick's **average** displacement, not the velocity after the
contact. A body hitting a wall after half the tick kept 5.6 u/s into the wall; two equal bodies .1 apart (12/0 u/s) got
8.6/2.6 instead of the inelastic 5.6/5.6.

New rule (Movement stays the only integrator and Position/Motion writer; achieved displacement is position only):
1. post-impulse velocity `Velocity` (Δv = J/Mass, unchanged);
2. every arena move reports up to two outward normals of surfaces that blocked planar travel (`CombatArenaMove.Wall0/1`,
   also a fully blocked grounded step); the body's velocity loses its inward component along them (frictionless: the
   tangent stays). This is the time-of-impact-consistent normal response: the displacement already stopped at the wall;
3. every body pair that violated in any solver pass of the tick and still touches at the end (gap ≤ `ContactGap` = 4·Skin,
   vertical overlap) exchanges its **approaching normal velocity** as a perfectly inelastic, mass-weighted collision
   (momentum conserved, kinetic energy never created). A held/frozen body is immovable (its partner loses the normal part).
   Sequential passes in sorted pair order (deterministic) until no change > `VelocityTolerance` (1e-5 u/s) or
   `MaxIterations`; after each exchange the blocked-surface clip is re-applied;
4. Coulomb deceleration on the final support (ground 24, air 2 u/s²), zero below 0.05 u/s (unchanged).

Consequences: no hidden normal velocity after a wall/corner/body contact at any time of impact; nothing is released when a
wall disappears or a contact frees; control, ally yielding and depenetration never become velocity (they are not in
`Velocity`); combined control+impulse keeps only the physical part; masses and chain transfer are kept (a 6- and 20-body
free chain transfers end to end). A tick without external motion has no velocity and is exactly the 04.3 tick.
Diagnostics: `VelocityContacts`, `VelocityIterations`, `VelocityResidual`. Position solver, budgets, holds, slop and
rollback are unchanged from #66.

### 1.2 PlayMode input FAIL (reported 56/66, reproduced on main `86012a0`)

Cause, from the executed log: `AdapterFollowsTheDefaultWorldWhenItAppearsOrIsReplaced` built "World B" **without**
`CombatArenaSystem` and loaded `CombatSmoke` (RequireArena) into it, so the production check rightly logged
"CombatSmoke requires CombatArenaSystem". The unhandled error failed the coroutine; its `finally` (restore default World,
dispose B) did not run, so the nine following input tests ran in the incomplete World B — the order dependency.
Fix (test preparation, production check untouched): World B now has the arena owner; replaced Worlds are restored/disposed
by the fixture's `[UnityTearDown]`, which runs after a failure. No Ignore/ignoreFailingMessages, no extra arena owner.

With the cascade gone, the full ordered run exposed what it had hidden:
- **Production lifetime defect:** `CombatArenaAuthoring` reported the frozen diagnostics of the interval when another lease
  had released the World as a failure of its *new* lease, before its geometry was even published. It now reports only
  after its staged geometry is published (`!state.Pending`).
- Stale tests: they waited for `Grounded` only; since #50 the published landing frames lock control and Jump (12 ticks), so
  taps/holds fell into the lock. They wait for the lock to end (`UntilFree`). The hold check moves left, away from the
  owner's central `Cube` obstacle in the scene.

### 1.3 Other links

- Whole-entity `Disabled` (Unity tag): invisible to every default query, so the `WithDisabled<InCombat>` cleanup missed it
  and a re-enabled fighter could release an old external velocity. Movement now clears `ExternalVelocity` for
  `Disabled` entities too (vertical semantics unchanged). Regression `WholeEntityDisabledFighterLosesItsExternalVelocity`.
- `process_native_delta` (471 MB at 400/wall_pressure in #66) was `Profiler.GetTotalAllocatedMemoryLong` across the measured
  owner loop **and** the test-only search comparison, with the Profiler enabled. The benchmark now splits it
  (Evidence/69): owner loop with Profiler on/off, comparison pass, and after-dispose. No leak claim either way without
  those columns; `gc_samples = 0` is not a native-memory claim.
- #66 limits (400 impulse_mass holds all bodies on impulse ticks; corridor ≈13 ms with held bodies; dense indexed search
  slower than scan) are re-measured on this solver in Evidence/69, not copied. Free small/medium chain baseline:
  `FreeSmallAndMediumChainsTransferWithoutHoldsOrDiscards` (6 and 20 bodies). Collision/slop were not relaxed.

## 2. Per-character body and strike data

### Where to edit (Inspector)

`Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset` → **Characters → [mage_female_01] → Combat**:

| Field | Unit / range (validated, never clamped) | Default (= previous built-in) |
|---|---|---|
| Body Radius | world units, 0.05..2 | 0.3 |
| Body Height | world units, 0.25..8 (constant in 05.1) | 2 |
| Body Mass | 0.05..100 | 1 |
| Hand1 / Hand2 / Foot1 / Foot2 / Physical Ability → Clip Id | a strike clip of this character with phases | punch_left / punch_right / kick_left / kick_right / kick_spin |
| … → Damage | HP, 0..1000 | 4 / 4 / 7 / 7 / 12 |
| … → Reach Min .. Reach Max | world units along facing, 0 ≤ min ≤ max ≤ 8 | 0 .. 1 / 1 / 1.3 / 1.3 / 1.45 |
| … → Depth Half Width | ±ΔZ world units, 0..4 | 0.4 |
| … → Height Tolerance | ±ΔY world units, 0..8 | 0.9 |

Timing is **not** here: each slot's clip keeps its Preparation/Active/Recovery frames, T and curve in `Animations`
(the only Active source). The config Inspector shows validation errors and the published table line per character.
The bootstrap (`Combat Bootstrap` in `CombatSmoke`) selects the character by **Strike Character Id**; the whole roster
uses that one character type (as before). Body/hit shape stay world units: pixels, nominal height and pivots never feed them.

### Owners and versioning

```
CharacterAnimationConfig (asset, Version++ on edit)
  └─ CharacterAnimationEntry.Combat (CharacterCombatSettings, m_Version)      edited data
CombatSmokeBootstrap: on change → validate → CombatAnimationSet (timing per slot clip) + ApplyTo
  └─ CombatStrikeSchedule.Publish  (the ONE existing table; Revision++)       published table
        ├─ CombatActionSystem: strike start copies Timing + Strike → CombatAttackState   frozen per execution
        │     └─ Movement (phase speeds), Hit (damage/box) read only that copy
        └─ BattleLifecycleSystem.Spawn: Body → CombatBody at start/restart   frozen per fighter
```

An edit publishes on the next frame and acts from the **next strike** (running strikes keep timing AND damage/box they
started with) and from the **next start/restart** for the body. An invalid value logs one error and keeps the previous
table. Worlds without a published table (tests, benchmarks) use `CombatStrikeSchedule.Default` = the previous values.

### Migration (idempotent)

`CombatAttackKind` keeps its stable values with semantic names: 1 PunchLeft→**Hand1**, 2 PunchRight→**Hand2**,
3 KickLeft→**Foot1**, 4 KickRight→**Foot2**, 5 KickSpin→**PhysicalAbility** (kick_spin is only its compatibility clip).
`CharacterCombatSettings` is versioned: data serialized before #69 (no block, or a zero block) gets the previous values
once (`OnAfterDeserialize`, version 0 → 1); version 1 is never rewritten. The project asset carries the explicit migrated
block (same values); animation T/curves/phases and Sprite pivots are untouched (pivots live in the atlas catalog/importer,
so an atlas refresh never resets combat data, which lives only in this config).

### Debug view

`Combat Bootstrap` → **Draw Combat Gizmos** (Play Mode, Scene view): body cylinder (team colour), facing, a line to the
support height while airborne, and the strike box frozen in the fighter's state — yellow Preparation, red Active, grey
Recovery (ReachMin..ReachMax along facing, ±Depth, ±Height around the point-target rule). Reads ECS only; no alpha
analysis, no renderer-driven damage, no frame preview.

## 3. Layout and series

| Input | 05.1 result |
|---|---|
| U | hand series: Hand1, Hand1, Hand2, Hand1, Hand2, … |
| I | foot series: Foot1, Foot1, Foot2, Foot1, Foot2, … |
| O | stand-alone physical ability (kick_spin clip, no self impulse yet) |
| Space | jump (kept until explicitly cancelled) |
| WASD / camera | unchanged |
| K, double-tap WASD, 7/8/9/0 | unbound (Stage 06 block, Stage 06 dash, Stage 13 magic) |

Input System capture → tick-aligned producer → shared queue → `CombatActionSystem`, identical for Player and AI. Old
Y/H/J/L and exact left/right actions are removed from the asset; renamed actions keep their GUIDs. The command bits of the
retired exact intentions (4..8) are rejected as `InvalidValue`: pre-#69 recordings that used them are **not compatible**
(test replays are regenerated from scripts; no stored replay depends on them).

Rules (manager's working interpretation for review, not an owner-confirmed fact):
- One press per tick, priority **U → I → O** (`CombatAttackTuning.StrikeButton`); the others of that tick are dropped.
- The sequence index counts **started strikes only**, in a bounded `SeriesStep` (0, 1, then 2/3 alternating): no limit of
  four, no overflow, no growing input buffer. Dropped, unexecuted or replaced (one-slot buffer) presses never advance it.
- Switching hand ↔ foot starts the other sequence at 1 at the next allowed chain point. Recovery ending without a
  continuation resets the series (a press on the End tick still chains; the next tick is free). No hidden timeout.
- O starts only from the free legal state, plays its full Recovery, is never buffered, never interrupts a strike and never
  evicts a buffered series press; feet never start it automatically.
- Unchanged: grounded start and Jump priority (Jump + strike → jump), landing lock, Windup drops, Active/Recovery one-slot
  buffer, chain delays, 50/25/25 phase speeds, facing lock except a strike's first tick.

## 4. Art and future boundaries

05.1 uses the current art (idle/run/jump/punch/kick/spin) and renderer; no pixel size or frame count is hard-coded (the
renderer still reads source_size/nominal_height/trim/pivots from metadata). The new atlas (#70) maps its clips through the
slot Clip Ids. The artistic contract (one animation per preview row, right-facing, physics provides world travel) and the
05.2/05.3/06/13 scope are recorded in ROADMAP.md.

## 5. Manual acceptance (owner, Play Mode in CombatSmoke)

1. U U U U U: punch_left, punch_left, punch_right, punch_left, punch_right; I I I…: kick_left, kick_left, kick_right…;
   pause between presses until idle → the next U is punch_left again.
2. U then I during Active/Recovery → the foot series starts at kick_left; O during a punch does nothing; O from idle
   plays kick_spin fully. Y/H/J/K/L do nothing.
3. Edit Combat → Hand1 Damage/Reach Max during Play: the next punch uses it (HP of the AI, red gizmo box); a running punch
   does not change. Body Radius/Mass: Restart battle (context menu) → gizmo cylinders and pushes reflect it.
4. Push into a wall/crowd with Test-only impulses: nothing re-launches after the contact; equal fighters pushed together
   slide together.
Result: NOT RUN by the implementer (interactive Editor of the owner).
