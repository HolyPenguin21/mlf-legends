# #64 — implementation bottom-up review and corrections

Reviewed starting local head: `ec3581f91488c7aaa1fe922fee0b72cb99f986a7`, integrated base
`adf98cd1c9ca4df2b2d2e96aa65d3a7e17eb3a5a`. This is the implementing agent's source review, not independent manager
acceptance. C#/Unity/Burst/scene/performance execution remains NOT RUN.

## Defects found and corrected

| Link / concrete trigger | Earlier behaviour | Correction / meaningful regression |
|---|---|---|
| Time-free body corrections → arena support → Motion → next Action/animation tick | A grounded ally temporarily displaced beyond a finite floor edge then returned to support stayed airborne, because each correction could clear Grounded but could not restore it. Next tick could report a false landing/input lock. | Reconcile final support for physically eligible actors after constraints, without time/gravity or a new landing. Discard pending landing if final support is absent. Four-body edge-pressure test requires a solved island (no rollback) and no LandTick on the following tick. |
| Complete enabled membership → Movement gathering → shared working grid | A fighter with Id/Body/Position/Team but without Motion or AppliedCommand could remain in the final spatial population while silently absent from contact solving. Invalid zero ID could also reach output before final-index validation. | Preflight active-battle expected/full/gathered populations and row identities before dispatch/ECS writes. Missing Motion/AppliedCommand/Team/Body tests require rejection, unchanged positions and an unreadable partial snapshot. Explicit no-session legacy fixtures remain compatible. |
| Controlled priority → free collinear ally group | Inverse-mass normal correction alone pushed a perfectly aligned ally forward forever. The controller could not actually pass it; accumulated group resistance was not equivalent to yielding through a group. | Add bounded mass-weighted transverse yielding under controlled inward pressure, .05 per tick at most/four probes, stable direction and opposite-side world probe. Same world/body budgets and next-iteration neighbour discovery. Free-ally test now requires actual passage; one back wall permits yielding, while back plus both side walls truly confine the group. |
| Unresolved constraints → rollback → diagnostics/benchmark | ResidualPenetration was sampled before rollback and only on failures, so it could describe a state that was never published, or show zero for a healthy skin-sized residual. | Record coherent final residual on a no-correction validation pass, and remeasure unresolved islands after rollback. ResidualComplete=false marks incomplete work-limit measurements. Keep observed unresolved/plane violations separately. Deep-overlap regression expects final penetration 4 after restoring three radius-2 bodies to coincident starts. |
| Body + world numerical skins → controlled run facts | A wall and a body each permit a skin-sized contact tolerance, so their combined settling can exceed the previous single-skin run threshold. | Use a central 4×skin controlled-distance epsilon. Confined-pressure regression requires no MoveStreakDistance accumulation while allowing bounded joint position tolerance. |
| Final index → replay/reference checks | New replay compared snapshots between Worlds but did not independently assert each published bound against the same tick's actual position/body. | Every compared tick now checks publication validity/tick, sorted IDs, team/body/position and independently constructed Min/Max bounds, preserving prior raw gameplay/HP/ledger assertions. |
| Paused Restart → diagnostic epoch | An old LastContacts.Tick can equal the new battle's current clock while no new solve has run. | Stamp the measured BattleNumber; paused-restart regression distinguishes old statistics from a current solve, without clearing/advancing a second clock. |
| Native/work/tuning → measurement fixtures | Numerical tiny-value guards were scattered; benchmark setup depended on entity enumeration for role placement. | Put all contact numeric guards in CombatContactTuning; sort actual IDs before scenario setup. Benchmark reports yield probes and final residual completeness as well as work/capacity/owner times. |

## Bottom-up connections checked

1. **Inputs/geometry:** finite actual Body dimensions/mass, disk vs AABB, vertical intervals, both moving bodies' relative
   motion, unique sorted pairs, deterministic coincident fallback; working capacity and rejection before writes.
2. **Native lifetime/work:** one Cells grid reused by the same owner; native row/pair/seen/diagnostic capacity reused;
   no ECS calls inside the explicit Burst job; complete pairs, bounded repeated discovery, correction/yield work,
   failure islands and conservative full rollback when enumeration/measurement budget is incomplete.
3. **World/body agreement:** every lateral/normal correction uses the arena volume/support/ceiling/slope path with
   integrateTime=false. Final support reconciliation changes kinetics/flags only, not position or elapsed time.
   The existing production arena Required/lease and failure paths are untouched.
4. **State owners:** Motion/Position still written only by Movement. Role comes from the current BattleSession, not
   Source. Initial overlap uses physical mass without role bias. Action/Facing locks, phase scaling, HP/hit ledger/combo,
   packages, roster, art/pivots and animation tuning remain unchanged.
5. **Outputs:** controlled distance never gains passive correction distance; no fictitious progress on rollback;
   final index rebuild follows all corrections before unchanged Hit. Runtime membership includes disabled enable bits
   and whole-entity Disabled through existing queries; no working rows survive finally, pause, End/Restart or owner disposal.
6. **Checks:** existing world/spatial/reference tests retained; authored source/chunk/pacing 1/2/3/10, lifecycle, air,
   landing and physical contact regressions inspected. Benchmark uses the same owners and records cold versus warm
   allocation/capacity and actual managed-kernel flag; runtime numbers are not inferred from source/model checks.

## Reproducible model controls — not C# execution

The verifier includes independent geometry/grid expectations and a graph-closure check: a rollback endpoint lies inside
its original working envelope, so an island rollback cannot introduce overlap with a surviving unconnected island.
The public snapshot checks remain independent arithmetic on actual recorded values, not calls to the production solver.

A separate **illustrative** two-disk model is in `Tools/Review-Stage04ContactPairModel.py`. It represents the response
choices and checks independent distance/speed bounds, but is not an independent oracle for C# or a run of the native job:

| 30 ticks, initial centres X=0/.6, controlled speed 4/s | Controller X | Ally X | Passed? |
|---|---:|---:|---|
| Earlier normal-only response | 1.666667 | 2.266667 | No |
| Bounded lateral response, open flat plane | 1.943573 | 0.882136 | Yes |

The same model checks phase-speed arithmetic with 50/25/25 multipliers. These are MODEL results only. Native float
rounding, world geometry, Burst/sourcegen and actual scene behaviour require Unity execution.

An independent 1-D pressure counterexample in the verifier uses three allies and a passive enemy, radius .3, centres
`.0995/.6995/1.2995/1.8995`, floor right edge X=1 and a 1/60 controlled step. Constraint arithmetic temporarily moves the
third body past X=1.3, then returns it to X≈1.299696 with correction work below budget. A sticky Grounded=false is wrong
at that final supported location. This isolates the state-consistency hazard; it does not certify the full C# regression.
The new C# integration uses an explicit zero-preparation/Active-speed timing fixture and landing=12, preserving project tuning.

The incomplete-input C# regression accepts either a direct exception or the logged managed-ISystem exception path from a
Burst thunk, while asserting no unrelated errors and restoring log settings in finally. Reference inspection of Unity's
published-source mirror (`Unity.Entities/SystemBaseRegistry.cs`, `WorldUnmanaged.cs`) motivated this dual-path test;
it is not a compile/runtime validation of the project's pinned Entities 6.5.0.

## Remaining acceptance and delivery

Independent source/Python checks are recorded against a clean exact commit in Evidence/64. Syntax parsing is not semantic
C# compilation. Authored geometry/integration/lifecycle/replay tests, Unity import/sourcegen/Burst, scene contacts,
Domain Reload off/build and CPU/GC/native benchmarks are NOT RUN here. Deep invalid initial packing can still freeze
with diagnostics; bounded iterations may conservatively stop large groups. Yield speed/priority are starting calibration.

No new architecture layer, system, grid, command, actor GameObject or Rigidbody was added. Stage 04 remains IN PROGRESS.
#62/#63 are merged. During this review the earlier push remained blocked by automatic approval review for private-code
egress, and no alternative upload was attempted. After the review the owner explicitly instructed commit, push and merge
of #64 (2026-10-29), authorizing the subsequent delivery.
