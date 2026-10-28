# Stage 04.1 / #58 — bodies and final-position spatial snapshot

Date: 2026-10-28. Base: `a6e96295c0a1e008327e28e8ff89205caf0a4f77` (fresh main, PR #57 already merged).
Branch: `feature/58-combat-spatial-index`. One PR to main; implementing agent does not merge.

#60 validation correction starts from merged main `180b4121792e68931f27c109b78cd0d24c21a4d4`.
The original base above remains the comparison base. PR #59 also included tuning commit
`68c43cbb51ff863ec5b7a5bac82e5f39b60c4db1`: atlas sprite pivots in
`Assets/MLFLegends/Art/Characters/MageFemale01/Atlas/mage_female_01_00.png.meta` and animation settings in
`Assets/MLFLegends/Combat/PresentationData/CharacterAnimationConfig.asset`. Git records HolyPenguin21 as author and
a Claude co-author trailer; this alone is not evidence of a separate owner approval. #60 explicitly preserves both assets.
The verifier pins their exact blobs (`2a531fc30a6cf9f402001841981b6915e96dc0b5` and
`c22eaf52b5213ecbc249aa878b700aa445b6b7e1`) rather than exempting Art/config directories.

**Stage 03: OWNER-ACCEPTED** from the owner's direct message recorded in #58: «Stage 03 закрыли. Идем дальше».
Its exact manual-test SHA/protocol was not supplied. This acceptance does not certify all historical Unity/GPU suites.
Stage 04 is IN PROGRESS; 04.1 is delivered for review, Unity-unverified. #46/#53 and QA #17/#25/#30 stay separate.
All older dated Stage 03 status/evidence entries are historical and do not override this current disposition.

## Data, defaults and responsibility

| Type / file | Reads | Writes / lifetime |
|---|---|---|
| `Core/CombatBody.cs` | — | Compact unmanaged Radius/Height/Mass, three floats. Per fighter. |
| `Simulation/CombatBodyTuning.cs` | — | Single spawn calibration: `Radius = 0.3`, `Height = 2`, `Mass = 1`; `Default` constructs the component. |
| `BattleLifecycleSystem` | Existing setup, body default | Existing archetype adds CombatBody; the same private Spawn initializes leaders/allies at 2/100/400. Existing End/Despawn/Restart destroys the entity/body. |
| `Simulation/CombatSpatialTuning.cs` | — | `CellSize = 1`. Work guards: `MaxBuildEntries = 16,777,216`, `MaxQueryCells = 65,536`. These never cap returned candidates. |
| `Simulation/CombatSpatialIndexSystem.cs` | Current enabled InCombat + Id/Team/Position/Body; Session/Clock | Only its native containers and diagnostics. No writes to position/motion, facing, HP, action state, clock or session. |

The cylinder's support/base is CombatPosition. X/Z disk radius = Radius, Y interval = `[Y, Y + Height]`.
It does not depend on art/pivot/trim/offset/pose/animation. Mass is stored and validated, without a second inverse-mass
field or physical influence in 04.1. After Spawn the entity's CombatBody is the actual size/mass; the index never reads
CombatBodyTuning or substitutes defaults. Valid setup changes to body values appear at the next rebuild.

All position/body scalars must be finite, body fields strictly positive; computed bounds must also remain finite.
`CombatSpatialBounds.TryFromBody` implements this validation. Invalid data, duplicate/zero IDs or an impossible grid
footprint fail with explicit exceptions and leave no readable partially rebuilt snapshot. No fighter is silently dropped.
An active partial World whose enabled fighters lack Body, Position or Team does not publish; no body/session/clock is manufactured.

## Geometry and API

World-owned uniform X/Z grid. Cell = `floor(world / CellSize)` in double arithmetic before checked-range conversion to
int; negative positions never truncate toward zero. Both insertion and query use the closed cell rectangle
`[floor(min / size), floor(max / size)]`, including a cell at an exactly touched upper boundary. Each body's entire X/Z
AABB footprint is inserted into every such cell; circle-corner extra candidates are intentionally conservative.
The optional refinement here is full closed AABB overlap on X/Y/Z. Tangent faces/edges/points count as overlap; no epsilon.
Long loop counters avoid int wrap at the last cell; cell-count products are checked before capacity changes.

Main-thread usage after State's index update:

```csharp
var index = world.GetExistingSystemManaged<CombatSpatialIndexSystem>();
// Allocate once for the consumer; reuse between queries/ticks and dispose when that consumer ends.
var results = new NativeList<CombatSpatialBody>(initialCapacity, Allocator.Persistent);
if (index.TryGetSnapshotInfo(out var snapshot) && index.TryQueryAabb(bounds, results))
{
    // results contains value copies, one per CombatantId, ascending Id.
    // For unique unordered pairs: retain only candidate.Id.Value > source.Id.Value.
}
```

`TryQueryAabb` clears the caller buffer even on rejection. Invalid/nonfinite/reversed query bounds throw. A query outside
representable cell coordinates, overflowing its cell-count product or exceeding MaxQueryCells uses the **complete sorted
snapshot** bounds scan. `LastQueryUsedFullScan` makes that rare fallback explicit. There is no candidate cap/truncation.
A build exceeding MaxBuildEntries/unrepresentable coordinates fails diagnostically instead of attempting unbounded work.

`TryQueryAabbBatch(queries, results, ranges)` performs the same queries atomically in one main-thread call. The caller
supplies a NativeArray of bounds, a reusable flat NativeList of body copies and a reusable NativeList<int2> of ranges
(`x = offset`, `y = count`). Every range is separately Id-sorted and duplicate-free. Membership is validated once for the
whole call; there is no callback, yield, read lease or escaping owner-memory view. All query inputs are validated before
any result is appended; stale/invalid input clears both outputs. Counters aggregate the batch and use long for entry visits/
duplicates. Only the appended range is sorted; later queries cannot reorder earlier ranges. Batches retain every range's
output, so dense output memory can grow with queries × candidates; callers may choose smaller batches. No candidate cap.

No managed List/array is created for each fighter or query. Deduplication uses reusable per-row query stamps, with a
clear on stamp wrap; rows/results sort by Id, independently of chunk/hash/insertion order. Consumers build pairs by the
single Id comparison above, not by the cell iteration order. Bounds queries are the only geometry API added.

## Tick, invalidation and memory

State order is **Action → Facing → Movement → SpatialIndex → Hit**. Attributes order the same systems in the default
World, ReplayWorld.RuntimeSystems and the benchmark. Hit retains its existing brute-force algorithm byte-for-byte.
Movement, Action, Facing, Hit, default attack/movement tuning, input/commands, packages, renderer/Inspector and scenes
are byte-unchanged from the original base. Art/config differ by the two pinned tuning assets above; no further production
code, asset or metadata change is introduced by #60. Documentation and test corrections are reported separately.

One successful build per actual SimulationClock.Tick and BattleSession.BattleNumber, after final integration. The guard
also prevents a second manually requested update from rebuilding the same tick. The existing clock and fixed-step rate
manager remain the only clock/accumulator. SnapshotInfo exposes Tick/BattleNumber; the owner instance determines World.

Every public read checks allocation/publication, live active session, matching tick/battle, a tick after BattleStartTick,
World structural EntityOrderVersion, exact enabled-fighter count, and the enabled bits of every published member. The last
check catches a disable/enable swap at equal count. Both expected fighter membership (Id + enabled InCombat) and complete
spatial membership must match, including a newly enabled partial fighter that has no Body/Position/Team. Validation is O(N)
per public read: **once for an entire atomic batch**, rather than once per fighter query. Single-query convenience calls
still each validate membership. Timing includes this cost. No bounds full scan is needed for membership and no allocation
is made; no sublinear total single-query guarantee is claimed.
Unrelated structural changes conservatively invalidate too: availability resumes on the next real rebuild. Body/position
value edits between ticks are setup changes, published only by the next rebuild, not silently patched into a final snapshot.

Paused End/Restart is seen immediately through the live session even without another fixed step; paused Despawn changes
the structural version and also rejects the old snapshot. Lifecycle cleanup now includes whole-entity Unity Disabled tags
as well as disabled InCombat bits, so no disabled actor survives End/Restart/Despawn. Scene switches use the same existing lifecycle path. An old
World/retained managed system returns false after disposal, without touching disposed native memory. Two Worlds have
separate storage, even when their Id/Tick/BattleNumber happen to match.

Only this system owns persistent `NativeList<CombatSpatialBody>`, `NativeParallelMultiHashMap<int2,int>` and
`NativeList<uint>` stamp storage. Capacity grows and is reused, no static registry, hidden singleton entity or renderer
ownership. OnDestroy releases all storage; partial allocation failures release already-created containers too. Invalidating
availability does not imply freeing capacity. The public API returns copies, **no borrowed native view**. Copies are usable
as current only until rebuild/membership/epoch change/disposal; callers must validate again and must not dereference stale
Entity handles. The caller owns/disposes its results list.

04.2/04.3 must extend this owner for proposed-movement broadphase, retaining Movement's sole position ownership; do not
create a second grid or publish predicted positions as final. Terrain/body contact resolution, impulses (04.4) and Hit's
switch to finalized candidates (04.5) remain outside this PR.

## Checks and performance handoff

Executed SOURCE / PYTHON MODEL checks are in `Docs/Evidence/58/static-checks.json`; rerun with
`python Tools/Verify-Stage04Spatial.py` (Python packages tree-sitter and tree-sitter-c-sharp). They syntax-parse all C#,
validate GUIDs/metadata and protected production-source identity (including the two exact tuning blobs), and compare a deterministic independent Python bounds scan with a
grid model under reversed insertion orders/unique-pair enumeration. It also checks 4,608 flat batch ranges, four deliberately
broken Python model variants, membership/cleanup counterexamples and extreme cell-count guards. These do not execute or
mutate the C# implementation. [Bottom-up review and corrections](STAGE04_BOTTOM_UP_REVIEW.md).

Authored Unity tests (**NOT RUN here**):

- `CombatSpatialIndexTests`: all invalid fields, finite/overflow bounds, tiny/large bodies, closed/negative cells, Y overlap,
  independent full scan (128 actors × 260 queries × two insertion/chunk orders), unique pairs, enormous fallback and errors;
  batch range equivalence/growth/mixed fallback, invalid/stale batch clearing, stamp wrap, extreme keys/count overflow and
  duplicate IDs / failed-build recovery.
- `CombatSpatialLifecycleTests`: actual lifecycle bodies 2/100/400, repeated paused Restart/End/Despawn, destroyed old entities,
  1/2/3/10 catch-up and duplicate-update guard, setup body changes and replay recording, enable/disable/count-preserving
  membership swaps, tick mismatch, independent Worlds/disposal/partial fixtures; missing each spatial component and newly
  enabled incomplete actors, independent clock/session prerequisites, whole-entity Disabled cleanup and second-owner rejection. Control replay disables only index updating
  and compares all old combat state for player/AI scripted movement/jumps/strikes against 1-tick pacing.
- Existing lifecycle AssertCombatant includes body defaults; replay records all three body values. Existing replay,
  default-world and benchmark order assertions retain unique-owner checks and add SpatialIndex between Movement and Hit.
- Default-world jump/pause test checks the snapshot's position after Movement, body default, paused Restart and End rejection.
  Existing scene-unload lifecycle tests continue to exercise the same session invalidation path.
- Existing baseline archetype guard now requires/allows CombatBody while retaining rejection of unrelated/managed types.
  The source verifier compares its exact list with the lifecycle archetype plus processor-added AppliedCombatCommand;
  the old reviewed-head omission is reproduced as a source counterexample.

`CombatStrikeBenchmarkTests.SpatialBuildAndQueriesForSparseAndDense2_100_400` extends the existing harness (no recorder/
renderer or new runtime profiling framework): 120 warm-up ticks, 600 measured ticks × 32 typical AABB queries; sparse/dense
at 2/100/400, both 32 single calls and one atomic batch with the same bounds. First build/capacity growth is separate.
Output `TestResults/performance/stage04-spatial.tsv` includes full fixed-tick mean/p95, build mean/p95 (including epoch/count
checks), query-batch mean/p95, candidates, duplicates, occupied cells/entries/capacity, native payload capacity **lower bound**
(excludes hash buckets/allocator metadata), caller-buffer capacity/bytes, process-wide Profiler native delta, GC.Alloc samples
with a positive control (recorder restored in finally) and
machine/package/Burst probe context. The spatial owner itself is managed SystemBase; other systems retain their existing
Burst paths. Capacity equality after warm-up is asserted; process-wide native deltas are not index-only allocation totals.
No linear dense-case guarantee: most/all bodies can become candidates. No CPU/memory/GPU measurement is claimed yet.

**NOT RUN:** Unity compilation/import, Entities sourcegen, Burst, EditMode/PlayMode suites, GPU/visual checks, actual GC/native
leak/performance runs, Domain Reload off and player build. Unity Editor/package runtime/device is unavailable in this
container. Historical Stage 03 logs do not certify 04.1. No observed source/model FAIL; runtime acceptance remains pending.
