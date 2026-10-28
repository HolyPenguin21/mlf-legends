# Stage 04.1 / #58 — bottom-up review of PR #59

2026-10-28. Reviewed implementation: `c73897d2453fe2c39ce6d6a55dfb84978825e484`, against base main
`a6e96295c0a1e008327e28e8ff89205caf0a4f77`. Corrections stay in the same branch/PR; no merge.
This is the implementing agent's second source review, not an independent manager review or Unity execution.

**#60 correction, 2026-10-28:** the historical review above concerns `c73897d`; the merged delivery also includes
`68c43cbb51ff863ec5b7a5bac82e5f39b60c4db1`. Its exact two tuning assets/blobs are listed in
[the spatial contract](STAGE04_SPATIAL_INDEX.md). The former blanket art/config identity claim and 177 unchanged-file
report do not certify merged main `180b4121792e68931f27c109b78cd0d24c21a4d4`: the old verifier actually fails on its
atlas `.meta`. Project-config test mismatches were source findings, not executed Unity failures. #60 preserves tuning,
corrects the existing tests and pins both tuning blobs. [Current evidence/reproduction](QA.md).

## Findings and corrections

| Finding | Trigger and effect in the reviewed version | Correction and authored regression |
|---|---|---|
| Existing baseline archetype guard rejects the new body | CombatBaselineTests.s_CombatantComponents omitted CombatBody while AssertLightCombatants rejects every unlisted type. The source contract therefore disagreed with Spawn and would fail the existing baseline suite; no Unity FAIL is claimed without execution. | Added Body to the strict required/allowed set, retaining unmanaged/archetype/extra-type checks. The source verifier now compares the whole lifecycle archetype plus AppliedCombatCommand against this list, and reproduces the original omission from c73897d. Runtime scope labels and body-byte metadata also updated. |
| Partial-World membership could silently omit a fighter | Expected query also required Position/Team. An enabled ID missing either was absent from both expected/complete counts, permitting an apparently complete empty/partial snapshot. Enabling a pre-existing partial actor without Body after publication also changed neither the complete query count nor structural version. | Expected query now uses only Id + enabled InCombat; both expected/complete counts are checked on rebuild **and public reads**. Tests remove each spatial component and enable an incomplete fighter after publication without another update. |
| Whole-entity disabled fighters survived lifecycle cleanup (pre-existing lifecycle gap exposed by this stage) | IgnoreComponentEnabledState includes disabled InCombat bits but still excludes Unity's separate Disabled tag. End/Restart/Despawn could miss such an actor, leaving its identity/body alive for a later re-enable. | The same lifecycle query adds IncludeDisabledEntities, without adding a cleanup owner. Parameterized paused End/Restart/Despawn tests assert actual old-entity destruction and body counts. |
| Repeated membership scans undermine broadphase scaling | Calling the single-query convenience method for N fighters validates N enabled members each time, producing O(N²) membership work even in a sparse scene. | Same-owner atomic TryQueryAabbBatch validates once per call, appends the same AABB results into caller-owned flat native storage and ranges. No second index, predicted positions, lifecycle/version owner or borrowed view. Tests compare every range with independent single-query results across growth, repeated/mixed fallback queries and invalid/stale input. |
| Geometry/failure/ownership edge tests were too narrow | Original tests did not exercise representable extreme keys, cell-count-product overflow, stamp wrap, valid→failed→recovered builds, duplicate IDs or second-owner rejection. The partial World omitted session and clock together rather than each prerequisite independently. | Added focused regressions for all those cases, retaining independent bounds/reference and existing replay/default-world owner assertions. These C# tests are authored, NOT RUN. |
| Benchmark did not expose all relevant costs | Initial benchmark measured build and 32 separate queries only; it did not show full fixed-tick cost or the caller-storage cost of retained batch output. Build timer omitted initial epoch/count checks. | Same existing harness now compares single/batch modes for sparse/dense 2/100/400, reports fixed-tick/build/query mean/p95 and caller capacities/bytes. Timer includes epoch/count checks. GC positive-control and measured recorder scopes both restore in finally. No numbers claimed without Unity. |

Batch output deliberately retains one range per query. A fighter may appear in several ranges, but never twice within one
range. Id > source.Id remains the unique-pair rule. Dense output can be O(queries × N), matching the actual candidates;
caller-selected smaller batches trade retained memory against validation frequency. Single-query API remains available.
Aggregate entry/duplicate diagnostics use long so a large batch cannot overflow the former int counters.

## Dependency audit

| Layer | Review result / boundary |
|---|---|
| Core / tuning | CombatBody remains three floats, initialized from one table; index reads actual ECS body values, no defaults, inverse-mass duplicate or art coupling. Finite/positive validation and finite computed bounds retained. |
| Geometry | Closed bounds and inclusive floor-cell ranges shared by insertion/query; X/Z body footprint covers every cell. Full X/Y/Z AABB refinement remains conservative for a vertical cylinder. Long iteration/count guards prevent wrap; huge queries use complete sorted scan. No clipping introduced. |
| Native memory | One managed World system owns body rows/grid/stamps. Grow/reuse, partial-allocation cleanup and disposal retained. Batch buffers/ranges belong to caller, sorting touches only the new range; no native view escapes or callbacks occur during validation/query execution. |
| Availability / lifecycle | Tick/BattleNumber/active session checked live, then structural order and complete enabled membership. Disabled-tag fix closes lifecycle cleanup gap. Paused Initialization cannot expose old entities; invalidation keeps capacity separate from availability. |
| Writers / phase graph | Action → Facing → Movement → SpatialIndex → Hit remains acyclic. Only existing lifecycle initializes Body; index writes no gameplay component. Existing Movement, Facing, Action, Hit and tuning files remain byte-identical to base. |
| Default / replay / benchmark | Same RuntimeSystems registration; unique-owner/order assertions retained. Replay body values included, previous-state control comparison retained. Default-world jump test reads post-Movement position; paused Restart/End rejection remains covered. |
| Presentation / scene / config | Renderer/Inspector/scene/packages unchanged. The merged delivery includes pivot `.meta` and CharacterAnimationConfig changes from 68c43c (two pinned blobs), preserved by #60. Scene exit uses the same lifecycle/session invalidation. No new clock, spawn path, cleanup system or character registry. |
| Future stages | No contact resolution, proposed-position index, impulses or Hit migration. Atomic batches supply the same conservative final-position AABB API for future consumers; 04.2–04.5 remain separate work. |

## Executed evidence and limits

`python Tools/Verify-Stage04Spatial.py` checks all 91 C# syntax trees, 208 unique GUIDs, 4,608 deterministic model AABB
queries and 12,048 source-body pair queries against an independent bounds scan; 4,608 flat batch ranges; four deliberately
broken **Python model** variants (lost upper cells, duplicates, ordering, stamp reuse); membership/cleanup predicate
counterexamples; extreme cell-count guards; exact Spawn/baseline allowed-archetype agreement (with the old-head omission
reproduced); protected-source identity and whitespace. Report with final source SHA256:
`Docs/Evidence/58/static-checks.json`. The corrected report records its tested SHA/tree, clean starting state, original
base, #60 base, full changed-path lists and exact tuning exceptions. Production protection covers Core/Simulation/Input/
Commands and assets/metadata; edited test sources and Markdown reporting are not claimed byte-unchanged.
Models do not execute or mutate the C# implementation.

Unity compilation, Entities sourcegen/Burst, C# EditMode/PlayMode suites, GPU, native leaks, actual allocations and benchmark
runs remain **NOT RUN**. A source-pattern check also rejects direct indexer writes on readonly using NativeArray locals;
mutable aliases retain the using storage as sole disposal owner. This is not a replacement for C# compilation. No source/model check proves Unity API compatibility or runtime timing. Managed owner cost,
conservative invalidation after unrelated structural changes and dense-case candidate/output cost remain explicit limits.
The next acceptance evidence must come from Unity on the corrected PR head; historical Stage 03 logs do not certify it.
