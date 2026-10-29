# Stage 04.3 / #64 — body contacts and yielding

Base: integrated main `adf98cd1c9ca4df2b2d2e96aa65d3a7e17eb3a5a`. On the owner's explicit instruction
«да, мержи все в мастер и начинай реализацию» PR #62 was merged as `589b627b457e4b78e43bae74be64b7c238224e02`,
then #63 was retargeted to main and merged as the base above. Their source trees were preserved.
Branch: `feature/64-body-contacts`, delivery through one PR to main. After the source review, the owner explicitly
authorized commit, push and merge (2026-10-29), superseding the earlier automatic publication block. Stage 03 OWNER-ACCEPTED;
Stage 04 IN PROGRESS. This is source delivery, not Unity/runtime acceptance.

[Bottom-up implementation review and corrections](STAGE04_BODY_CONTACTS_REVIEW.md) supersedes earlier notes where noted.

## Owners and execution boundary

Action → Facing → **Movement: gather all inputs → proposals → contact/world solve → write all outputs** →
**SpatialIndex: publish final current-tick snapshot** → Hit. No extra system, position writer, clock or command source.

- `CombatMovementSystem` remains an ISystem and the only runtime writer of Position/Motion. Its OnUpdate now has an
  explicit **managed orchestration** boundary (no BurstCompile annotation). It gathers the enabled inputs before any
  fighter position is written, resolves the current BattleSession.HumanCombatant role, calls the existing owner,
  then publishes the results. Action/Facing/Hit, their locks/windows/HP/ledger, input and lifecycle code are unchanged.
- `CombatSpatialIndexSystem` remains the one World native-memory/grid owner. It owns persistent movement rows,
  pairs, diagnostics, an allocated empty-shape view for bare fixtures, and the **same existing** Cells/Seen containers.
  Rows and pairs are cleared on entry and in a finally block on exit; no new scratch Entity reference survives a call.
  Capacity is retained until owner/World destruction; every native container is disposed, including partial construction.
- Before dispatch, the owner sorts input rows by CombatantId and reserves conservative grid and complete pair capacity.
  NativeList growth is cold/watermark allocation, not per-pair/per-tick allocation. Bounds growth covers initial world
  depenetration, proposed movement and the bounded sum of body corrections. Active-battle membership is preflighted
  against every enabled ID: a missing Motion/AppliedCommand/Body/Team or invalid ID rejects the whole call, rather than
  silently excluding a physical body. Isolated no-session fixtures keep their explicit compatibility path.
  Unrepresentable input/capacity throws
  explicitly before ECS output; a partial snapshot cannot be read.
- `CombatContactJob` is one `[BurstCompile(CompileSynchronously=true, FloatMode=Strict)] IJob`, explicitly
  **Schedule().Complete()** before output writes. All proposals, repeated working-grid builds, pair sorting and numeric
  contact/world constraints run in that job. The pure numeric `TryCells` was moved to `CombatSpatialBounds` so the job
  calls no managed SystemBase API. The job never calls EntityManager or writes components.
- `KernelRanManaged`, set only by a BurstDiscard diagnostic, exposes actual execution when Burst is disabled. There is
  no claim that source inspection proves Burst compilation. Benchmark records this flag plus gather/write-inclusive
  Movement time and the separate schedule+completion kernel duration; those times overlap, they are not additive.

## Working grid versus final snapshot

BeginMovement invalidates public publication before reusing Cells/Seen. Working rows contain original positions and
proposed/corrected positions, never last tick's final state masquerading as this tick. Public Tick/BattleNumber/live
membership/order guards are retained. No borrowed working API is public and no consumer can query a working snapshot.
After Movement writes every output, the existing index update rebuilds Cells from actual final ECS positions and
publishes the snapshot before Hit. Hit's current full-search implementation is unchanged (migration is 04.5).
An explicitly disabled publication owner still supplies synchronous contact scratch to Movement; it publishes nothing.

At every solver iteration the working grid is rebuilt with the **union of original and current proposed body AABBs**,
including both bodies' motion, full radii and Y intervals. Candidates are deduplicated per source; unique index pairs
are sorted, equivalent to `(minId,maxId)` because all input rows were sorted. Corrections cannot rely only on original
neighbours: the next iteration discovers newly touched bodies. Final publication reuses the grid rather than keeping
a parallel body grid. Existing AABB query/reference tests and stale-snapshot rules remain intact.

## Geometry and response

Exact vertical cylinders: X/Z disk distance plus open Y-overlap for response. AABB is only a candidate filter.
The relative-motion disk quadratic is intersected with the relative vertical-interval time slab, so fast opposing
motions/exchanging sides are detected even when final endpoints do not overlap. At first contact the supporting-plane
constraint prevents crossing; it is conservative about sliding around a cylinder within one tick. Coincident centres
use an ID-derived axis, not hash, Entity or chunk order. This solver never makes another body a support surface.

For allies, separating displacement shares are inverse-mass proportional, computed from the live CombatBody.Mass
without storing an inverse-mass component. Locomotion priority is a separate **mobility factor .2** for the currently
controlled role (ordinary ally mobility 1). Equal-mass free allies therefore yield approximately 5/6 of an inward step,
leaving approximately 1/6 as resistance before lateral clearance. This is explicit starting calibration, not final balance or AI navigation.
A normal priority alone cannot let a collinear controller actually pass an ally. Non-initial controlled pressure also
requests a mass-weighted transverse yield: at most 3 units/s (0.05 per tick), at most four world probes per ally/tick.
Existing transverse offset chooses the direction; an ID-derived tie break handles a perfectly aligned row. If the
preferred side is blocked, one bounded opposite-side probe is allowed. All displacement goes through the same arena
geometry, correction budget and subsequent body-grid iterations; no membership/solid collision is disabled. This is
contact response, not AI/navigation or a second movement command. Frozen actors cannot initiate/receive yielding.
Initial overlap uses physical mass shares without role bias. An immovable/failed world proposal has zero mobility.

For enemies, ordinary inward locomotion is cancelled on the fighter(s) generating that inward displacement; it is not
converted into passive enemy motion. Retreat and tangential motion remain available. Sustained input cannot bulldoze a
passive enemy, even with a large mass. Initial overlap and vertical contact entry use depenetration rather than this
locomotion rule. External impulse/knockback/stagger are not introduced: 04.4 will provide those physical inputs separately.
Changing Player/AI command source does not change contact priority; the same role/commands give the same physics.

## World consistency, landing and presentation

Each numeric body correction reuses `CombatArenaGeometry.Move(... integrateTime:false)` with the already integrated
proposal's velocity/support. This uses the same world volume/side/slope/ceiling checks and world substeps, without
another gravity integration, jump edge, phase update, input press, Hit or clock. Support is checked after displacement;
leaving an edge marks airborne immediately. For an edge crossed solely by a correction, gravity resumes next tick,
rather than inventing additional elapsed time inside the constraint iterations. World-following cannot put the head
through a ceiling or provide an artificial support on a fighter. Required/lease/revision/failure guards remain.
The ordinary world proposal retains its existing vertical-before-control ordering and landing contact detection.

Final arena support is reconciled after the constraints. Actors that were physically grounded (or grounded in their
world proposal) can recover support lost only during intermediate iterations, without creating a landing. If the final
position is unsupported, a pending landing is not committed. An actual airborne proposal is never grounded just because
of an internal body iteration. No new vertical integration is performed by this reconciliation.
A physical landing result copies the existing duration only once; intermediate corrections cannot manufacture a new landing. A landing lock drops controlled input but allows physical yielding/depenetration.
No contact writes Facing or cancels Attack. Preparation/Active/Recovery speed factors are applied once, before proposal.

ControlledDistance starts with the world proposal's actual travelled distance (initial world depenetration excluded).
Opposing corrective displacement subtracts its projection onto the commanded direction, clamped at zero. Positive or
orthogonal passive displacement never increases this value; passive actors therefore do not acquire run distance.
Controlled run facts use a 4×skin distance epsilon (.0008) to absorb combined world/body contact tolerance rather than
counting their tiny numerical settling as running. An island rollback publishes neither displacement nor fictitious run progress. LastMoveTick and the existing six-tick
run grace still apply: stopping may visually retain the prior run briefly, but cannot advance stride distance.

## Limits, failure visibility and determinism

`CombatContactTuning` centralises skin .0002, role mobility .2, passive/initial correction budget min(Radius,.5),
four yield probes and a 3 units/s transverse yield cap per ally, numeric near-zero guards,
48 iterations, 4,000,000 pair visits and 32,000,000 grid candidate visits per call, and the existing battle cap of 400.
A fighter's sum-of-achieved-corrections budget is its desired distance plus that initial/passive budget. No pair-cap truncation:
complete potential pair capacity is reserved. Domain/body/world limits remain those of 04.2 in production.

Every candidate edge joins a deterministic union-find island, including newly discovered neighbours. After bounded
iterations, any remaining violated island is rolled back to original positions/kinetics; distant islands can proceed.
All rollback positions were included in candidate envelopes, so rollback cannot insert a new unconnected overlap.
A work-limit hit rolls back the full call (candidate enumeration may be incomplete). Deep pre-existing overlaps can
therefore remain frozen and diagnosed rather than teleport apart. Large trapped groups can conservatively stop as an
island; this is a bounded positional solver, not an unbounded packing optimiser or navigation/side-stepping AI.

LastContacts reports Tick and BattleNumber (old stats remain explicitly in their old epoch across a paused Restart), candidates, pair visits, peak unique pairs, iterations, frozen bodies, world rejections,
actual final maximum penetration (including skin and rollback), a ResidualComplete flag, observed unresolved penetration/
swept-plane violation at failure validation, yield probes, work-limit status and actual managed-kernel flag.
At work-limit exhaustion, ResidualComplete=false explicitly means the final penetration was not fully measured; a zero
numeric field is not a claimed zero penetration. Healthy no-correction validation records the coherent final residual;
unresolved islands are measured again after rollback within the same pair-work budget. A failure is visible; no truncated candidate set or unresolved new crossing is accepted.
Candidate-connected islands can include nearby noncontacting bodies: rollback is deliberately conservative.

Determinism depends on the same IDs, numeric inputs, role and arena revision. Input rows and unique pairs are ID ordered;
all proposals are formed first. Hash enumeration only fills a subsequently sorted pair list. The only authoritative
clock remains SimulationClock at 60 Hz. No hidden per-entity contact history crosses a tick or battle.

## Checks and measurement

- `CombatBodyContactTests`: independent circle/Y examples, fast finite-cylinder exchanges, real Movement small-body
  exchange, passive enemy pressure/no run progress, free/wall-trapped allies, unequal mass, coincident centres, actual
  sizes, airborne height, landing yielding, newly introduced third neighbour, opposing corridor groups, ramp+ceiling,
  finite-floor edge, transient edge-support return without a false landing, incomplete-input rejection,
  actual lateral passage/opposite-side yield and genuinely confined wall pressure, chunk/source/pacing 1/2/3/10, pause/disabled/Despawn/Restart/End and owner replacement.
- Replay records real final snapshot values separately from existing raw gameplay logs. Snapshot logs use round-trip
  scalar formatting; actual Position/Motion equality is checked each tick. Publication-disabled control keeps the
  original read-only-index gameplay assertion; it is not confused with disabling physical contacts.
- Existing free-speed direction and uninterrupted landing/run-stride fixtures move the unrelated enemy away. Hit-box and multi-target oracle fixtures use
  explicit small **collidable** body radii (including the 2/100/400 hit-ledger rows), keeping independent original reach/depth/height/damage/ledger assertions.
  World-contact and spatial/reference tests are retained. No tests Ignore'd or production collision membership removed.
- `CombatContactBenchmarkTests` extends the existing benchmark World with 2/100/400 × sparse, dense allies, opposing
  enemies, corridor, wall-pressure scenarios. Scenario/role placement sorts actual IDs before setup, independent of chunk order. It records cold/warm native capacity, Movement/kernel/index/Hit/full tick
  mean/p95, candidates/pairs/iterations/failures/residuals, CPU times, managed-kernel flag, positive-control-validated GC
  samples and process native-memory delta. Results go to stage04-body-contacts.tsv when actually run. Dense cases are
  not promised linear. No CPU/GC/memory/FPS measurement is inferred from source.
- `Tools/Verify-Stage04Contacts.py` is explicitly source/Python-model verification, not C#/Unity execution. It pins the
  integrated base, checks protected production identity, syntax/metadata/owners and independent geometry/grid models.
  Stored evidence records the actual tested commit/tree, not a self-referential future evidence commit.

**Unity import/C# compile/Entities sourcegen/Burst, authored EditMode/PlayMode/scene, performance/leaks/build/Domain Reload
off: NOT RUN here.** No Unity/C# toolchain is installed. Source/model results are reported separately in Evidence/64.
Manager review and owner manual contacts acceptance remain pending; Stage 04 as a whole is not closed.
