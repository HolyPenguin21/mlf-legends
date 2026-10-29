# Stage 04.4–04.5 / #66 — external impulses, indexed Hit and 2/100/400 scale

Base: fresh main `cf2b5bcf57c2b81b635074a0322a85ecf50879b3` (merge of PR #65 / #64). Branch
`feature/66-impulses-indexed-hit`, one PR to main. Merge only on a separate owner instruction; Stage 04 is not closed by
this report (manager review and owner manual acceptance remain). Stage 03 OWNER-ACCEPTED; Stage 04 IN PROGRESS.
Commit order: 04.3 blocker fix → physical mechanism → indexed Hit → integration/checks/docs.
Results and the exact tested SHA: [Evidence/66](Evidence/66/README.md).

## 0. Base check and blocker found first

The issue's base had never been compiled. The first real Unity 6000.5.4f1 batch run of clean `cf2b5bc` stopped at
**CS0246 in `Assets/Tests/EditMode/Combat/CombatReplayDriver.cs:619`** (`float3` without `using Unity.Mathematics`, added
by #64's snapshot recorder). Runtime assemblies compiled; the whole EditMode test assembly did not, so no 04.3 test had
ever run. Fixed by the first commit (one `using`); the 04.3 suite results on that fixed base are in Evidence/66.
Nothing else in 04.3 was reworked without an executed reason.

## 1. Dataflow and owners

commands → Action → Facing → **Movement** (gather all rows → apply pending impulses → physics/world proposal → control
proposal → body/world constraints → velocity from achieved physics → friction → write all) → **final SpatialIndex** of the
tick → **Hit** (managed gather → one atomic index batch → Burst kernel → HP/ledger).

| Data | Owner / writer | Notes |
|---|---|---|
| `CombatImpulse` buffer on the `CombatImpulseQueue` singleton | created/destroyed by `CombatMovementSystem`; producers append | the only impulse input; consumed by Movement |
| `CombatMotion.ExternalVelocity` (float2 X/Z, units/s) | `CombatMovementSystem` only | separate from commanded locomotion; zero = 04.3 behaviour |
| `CombatMotion.VerticalVelocity` | `CombatMovementSystem` only | still the only vertical velocity (jump, gravity, impulse Y) |
| Position | `CombatMovementSystem` only | unchanged rule |
| HP / hit ledgers | `CombatHitSystem` only | unchanged rule; Action still clears the ledger at strike start |

No second integrator, physics clock, command queue, PhysicsManager, event framework, hit grid or position writer.
Mass is read from `CombatBody.Mass` only; inverse mass is never stored.

## 2. Impulse input, tick and epoch

- An entry is `{Target CombatantId, Impulse J float3, BattleNumber}`; `CombatImpulse.Submit(EntityManager, id, J)` stamps the
  current battle. Δv = J / CombatBody.Mass: X/Z into ExternalVelocity, Y into VerticalVelocity.
- **Boundary:** everything in the buffer when Movement of tick T runs is consumed by that call — applied, dropped or
  rejected — and the buffer is cleared after a successful solve and write. An entry appended after Movement T (a future
  Hit-phase producer, tested with `AfterHitImpulseProducer`) is applied in Movement T+1: it neither moves the target in T
  nor changes the snapshot of T. Catch-up ticks cannot reapply it: the first tick consumed it.
- **Order:** several entries for one target add up in buffer (submission) order; none overwrites another.
- **Validation (no clipping):** non-finite J/Δv, a non-positive mass, a resulting |X/Z external velocity| above
  `MaxExternalSpeed` or |vertical Δv of the tick| above it → the entry is **rejected** and counted; it never partially
  changes velocity. Another battle's epoch, an unknown/despawned/out-of-combat target → **dropped** and counted.
- **Pause** runs no Movement: input waits. **Restart/End while paused**: the next Movement drops the old-epoch entries.
  **New World**: its own empty buffer. **Disabled InCombat**: entries to it are dropped and Movement clears its
  ExternalVelocity, so re-enabling never releases an old impulse (its vertical state keeps the existing 04.2 behaviour).
  **Despawn/End/Restart** destroy the entity with its Motion. An exception in Movement leaves the input unconsumed.

## 3. Integration at 60 Hz (`CombatImpulseTuning`, the only place of the coefficients)

Per Movement tick, for each fighter:
1. pending J → Δv;
2. if no external motion and no lift: **exactly the 04.3 proposal** (one world Move);
3. otherwise a physics world Move with time first (gravity, Jump, vertical lift, landing, displacement ExternalVelocity·dt
   through the same ArenaGeometry substeps), then the commanded proposal from there without time (`integrateTime:false`).
   Strike phase multipliers 50/25/25 and the landing lock act only on that control proposal; physics is never scaled or
   locked. A landing in the physics move still blocks the control step of that tick;
4. body/world constraints (section 4);
5. new X/Z velocity = achieved **physical** X/Z displacement / dt (walls and blocking bodies remove normal motion,
   free tangential motion stays — walls are frictionless);
6. Coulomb deceleration from the final support: ground 24 u/s², air 2 u/s²; below 0.05 u/s → 0.

| Tunable | Value | Meaning |
|---|---|---|
| `MaxExternalSpeed` | 24 u/s (0.4 per tick) | accepted max of X/Z external speed and of one tick's vertical Δv; matches world substeps (≤256 for a radius-0.01 body) and the reserved swept margin |
| `GroundDeceleration` | 24 u/s² | 6 u/s stops after ~15 ticks / 0.75 units |
| `AirDeceleration` | 2 u/s² | light drag while airborne |
| `StopSpeed` | 0.05 u/s | rest threshold |

Vertical: a positive lift on support leaves the ground like a jump (no Jump Pressed, no attack/animation system), then the
existing gravity/ceiling/support/landing rules apply once; a lift too small to beat one tick of gravity creates no landing.
A downward impulse on support is absorbed. Airborne lift adds to VerticalVelocity. Movement never writes Facing or
AttackState; passive displacement never increases ControlledDistance/MoveStreakDistance/LastMoveTick (the control step
alone produces run distance). No hitstun, stagger, interrupt or lock is added.

## 4. Impulse, mass and joint contacts

Each body row carries the achieved physical displacement `Physical` (world move of the external step plus physical contact
shares). For every violated pair the swept depth is split by cause:

- **physical share** f = physical inward approach / (physical + other inward approach), zero on initial overlap and when no
  body has physical displacement. That part is resolved as a **perfectly inelastic, mass-weighted normal response for any
  pair of teams**: corrections in inverse-mass proportion (momentum-conserving), added to `Physical`, hence to velocity.
  The controlled role's .2 mobility and ally yielding never apply to it — physics gives the player no privilege;
- **the rest** keeps the exact 04.3 rules (ally mobility/yield, enemy inward-locomotion cancellation, initial-overlap mass
  depenetration). The enemy rule now measures inward *locomotion* as displacement minus `Physical`, so an impulse is
  never cancelled as "bulldozing". With no external motion f = 0 and the solver is the 04.3 solver.

Consequences: an enemy blocks ordinary walking but receives physical transfer; transfer propagates through a chain of any
masses/teams via the repeated pair discovery; contacts create no kinetic energy in isolated passive examples; initial
depenetration, yielding and locomotion corrections never become velocity; pressure into a wall/blocked body leaves no
stored velocity to "shoot out" later; ramps, edges, ceilings and support use the same ArenaGeometry calls. No ragdoll, no
restitution, no rigid-body rotation, no secondary HP damage, no friendly damage or stagger.

**Budgets/envelope.** A body's sum-of-corrections budget adds |External| and, when the call has any physics, one tick of
`MaxExternalSpeed` for transfer. The owner reserves the working-grid margin from the same terms
(`2|Desired| + 2·CorrectionBudget + 3·max(MaxExternalSpeed, fastest current speed)·dt`), so the swept bounds and
capacity cover impulse motion of both bodies and group transfer. Collisions, culling and pairs are never skipped.

**Failure/rollback.** A frozen (unresolved/work-limit/world-failed) island publishes its original Position/Motion with
**zero external velocity**; the consumed J is not retried and is counted in `DiscardedVelocities`. A velocity above the
supported maximum produced by transfer is counted in `OverSpeedBodies` (diagnosed, not clipped; the next world move's
substep guard decides). Diagnostics per call: `ImpulsesApplied/Dropped/Rejected`, `PhysicalBodies`, `DiscardedVelocities`,
`OverSpeedBodies`, `MaxExternalSpeed`, plus the existing residual/completeness/work-limit fields.

## 5. Hit on the final shared index (04.5)

- `CombatHitSystem` (ISystem) has **managed orchestration**: it gathers Active attackers into persistent native scratch,
  sorts them by CombatantId, builds one point-target strike AABB each and calls
  `CombatSpatialIndexSystem.TryQueryAabbBatch` once (membership validated once per batch). A **Burst `CombatHitJob`**
  (Schedule+Complete) runs the unchanged rules on the Id-sorted candidate ranges and writes HP/ledgers through lookups.
  The managed index API is never called from Burst; `KernelRanManaged` reports actual execution.
- Broadphase AABB = the old point rule's box: X from facing and ReachMin/ReachMax, Z ±DepthHalfWidth, Y ±HeightTolerance,
  padded by ~1e-6 relative so float rounding inside `Inside` cannot fall outside. Large body bounds only add candidates;
  the radius never widens the hit rule (target point = CombatPosition).
- Unchanged: closed bounds, Active timing frozen at strike start, both facings, enemy/self filters, every valid target,
  Damage with HP clamp at 0, once-per-strike ledger and its reset, HP=0/simultaneous strikes, no defeat.
- A missing or stale snapshot with Active attackers throws `InvalidOperationException` **before any HP/ledger write**;
  there is no brute-force fallback. Ticks without Active attackers do not query.
- Scratch (attackers, queries, candidates, ranges, diagnostics) belongs to the Hit system of that World, grows only as a
  capacity watermark, is cleared after every call (no Entity survives a tick) and is disposed in OnDestroy. No per-tick
  copy of the roster into Temp arrays. `LastHits` reports ActiveAttackers, Queries, Candidates, EntriesVisited,
  InsideTests, AcceptedHits, UsedFullScan and KernelRanManaged.

## 6. Checks (authored; results in Evidence/66)

- `CombatImpulseTests`: Δv=J/m for two masses and several ordered impulses; analytic coast/stop; zero impulse equals
  the 04.3 record; controlled vs AI parity; strike phases/landing lock scale only control; lift/low ceiling/single landing;
  enemy blocks walking but takes mass-weighted inelastic transfer (momentum/energy); mixed-mass/team chain without energy
  gain or overlap; wall normal removal with tangential slide and no stored release; finite floor edge, ramp, corner;
  fast opposing bodies and new neighbours; speed-limit rejection without clipping; rollback discard without repeat/NaN;
  pause/Restart/Disable/Despawn/new World; Hit-phase producer → Movement T+1 with unchanged snapshot T; chunk order,
  source and pacing 1/2/3/10 replay including external velocity and pending input/epoch.
- `CombatIndexedHitTests` + `CombatHitOracle`: an independent brute-force oracle (own box rule, every fighter) runs after
  the snapshot and before Hit on the same final positions and checks HP/ledger per tick: random sparse/dense layouts at
  2/100/400 across negative cells and height layers, exact closed bounds for both facings, big radius with point outside
  reach, several targets/Active ticks/new strike, stale snapshot failure before writes. The 04.1 "index disabled"
  control is replaced by this oracle (a disabled index is no longer a valid Hit input). Stage 05.1 suites are unchanged.
- Replay records ExternalVelocity and pending impulses with their epoch. `CombatBaselineTests` counts the impulse input
  as the World's sixth own entity.
- `CombatContactBenchmarkTests` (existing infrastructure) adds `impulse_single`, `impulse_mass`, `strikes` to 2/100/400 ×
  sparse/dense/opponents/corridor/wall; records impulse/rollback counters, active-attacker ticks, accepted hits, Hit
  queries/candidates/entries/Inside tests, managed-kernel flags, and compares a complete scan with the index batch on the
  same snapshot (must match). Writes `TestResults/performance/stage04-impulses-hit.tsv`. Dense work is not linear; no
  FPS is promised. Old benchmark numbers are not carried over to this solver.

## 7. Manual Unity checklist (owner)

Inspector of `CombatSmokeBootstrap` in Play Mode → **Test-only impulses** (no new keys; WASD/camera unchanged):
1. Push controlled ±X into a wall and into a crowd: no tunnelling, crowd shoves by mass, nothing re-launches after.
2. Push into an enemy while walking: walking alone never shoves; the impulse does.
3. Lift controlled; jump/attack/landing during a push: attack and facing stay, landing lock still blocks only control.
4. Blast: everyone away from the controlled fighter; sprites follow support on ramp/edge, extreme poses/bounds/culling
   stay correct; off-screen fighters keep simulating.
Result: NOT RUN by the implementer (needs the owner's interactive Editor).

## 8. Stage 05 remainder

Already delivered (do not reimplement): five base strikes, series/buffer, Windup/Active/Recovery phases, HP and
once-per-strike ledger, now resolved on the shared final index. Remaining Stage 05/06 scope: per-attack Impact values
wired to this impulse input, stagger/launch as combat effects, interrupt/cancel, guard damage, jump attacks,
heavy/light mapping, defeat and balance.

## 9. Measured limits (see Evidence/66)

- Dense pressing fronts: at 400 the opposing-front layout is rolled back on every tick (also without impulses); at 100
  a single impulse into the front leaves it frozen on every later tick. The diagnosed bounded fallback works as
  specified, but convergence for large pressing groups needs a decision before Stage 04 is closed.
- Indexed Hit search is faster than a full scan in sparse layouts and slower in dense 400 packs (managed batch query
  cost). Correctness matches the brute-force oracle in every measured and tested case.
- Burst-off benchmark measured all scenarios but exceeded the 1800 s test timeout (FAIL by timeout). Other Performance
  suites and the owner's manual checklist: NOT RUN.
