# Bottom-up review of #73 and #74

Reviewed starting from PR #76 head `124e6505c581b748b3c5c73076934a89d390fc15`, on merged #73 base
`78b473b496a22296a169c1b1b05363174dacb69e`. This is a source review with corrections, not Unity execution or owner acceptance.

## Defects found and corrected

| Severity | Defect and consequence | Correction and regression coverage |
| --- | --- | --- |
| High | Ordinary point attacks scheduled CombatHitJob with a default/unconstructed Segments NativeArray. Job safety validation can reject the entire job, even though its swept branch is unused. | Supply a zero-length view of the spatial owner's persistent motion buffer, with a valid safety handle. Ordinary positive/zero-damage tests exercise the scheduling path and require zero trace count. |
| High | SolveMotion treated every WorkLimit as a failed position solve, including failure only in final velocity resolution. It also treated exactly spending the budget as failure. This rolled back valid ordinary movement and contradicted the pre-existing #70 velocity-budget regression. | Keep positions when ResidualComplete is true; retain the solver's discarded-velocity result. Exact budget consumption alone does not set WorkLimit. Keep VelocityPassWorkLimitKeepsSolvedPositionsAndPublishesNoPartialVelocity; add ExactlySpentPairBudgetStillPublishesTheCompletedSolve. |
| Medium | Each remaining ability slice received at least one new pair visit after the shared tick budget ran out. | Do not schedule further contact kernels with an exhausted budget; represent the remaining intervals as explicit holds. SlicedAbilityDoesNotReplenishAnExhaustedTickBudget checks the single refused-visit bound and all 32 intervals for two actors. |
| Medium | The wrapper reset solver Frozen back to the initial value even on the ordinary one-step path, so Movement could publish rolled-back Motion instead of preserving its authoritative frozen state. | Preserve the solver flag on the ordinary path. Sliced solves alone retry from accepted slice starts; existing contact/island/failure tests remain required. |
| Medium | TryMotionBatch did not reset/publish query diagnostics. Hit inherited stale/zero entry counts and could report that its all-roster motion scan was not a full scan. | Publish actual scan count, candidates, elapsed time and full-scan flag. The crossing-target test now requires a scan of both motion envelopes. |
| Medium | Static sprite GPU fixtures paused Movement but not the new nearest-facing owner. Fixed updates during yields could replace their prescribed away-facing poses, invalidating flip/depth image expectations. | Pause Facing for this renderer-only suite and restore its previous enabled state in teardown. Gameplay facing integration tests continue using the real owner. |

The ordinary Hit array and wrapper defects originate in #73, already merged into main. Their corrections travel in #76;
main does not contain these fixes until that PR is merged separately by owner instruction.

## Connections inspected

| Layer | Review scope and result |
| --- | --- |
| Data and timing | Spawn base vs accepted body height; frozen strike values; authoring-to-tick mapping; default neutral hands/feet; Active clipping. No additional confirmed defect in this pass. |
| Owners and order | Lifecycle reset; accepted command input; Action landing progress; sole Facing writer; launch after Facing; Movement/profile acceptance; final index guards; Hit ledger/HP and T+1 impact queue. Corrections above apply. |
| Motion and geometry | Id-sorted synchronized intervals, relative sweep, waist offsets once, body/envelope bounds, wall/ramp occlusion, shared budget and freeze policy. Corrections above apply; numerical runtime acceptance remains open. |
| Feedback and lifecycle | Positive confirmed damage including HP0; ledger prevents refresh; entity-keyed extraction through page sorting; simulation-time fade; restart/despawn/World teardown. No additional confirmed defect in this pass. |
| Presentation | CPU/GPU instance layouts, tint after alpha clip, shadow plane/footprint/depth/bounds, resource disposal and World rebinding, Edit Mode gizmo gates. Graphics fixture correction above; no GPU result claimed. |
| Tests, diagnostics, docs | Old contact budget contract, new policy vs old image fixtures, truthful motion query statistics, stale historical Facing text in SIMULATION.md. Corrected and linked to current policy. |

Added CompactWaistFlowsThroughMovementBroadphaseAndPointOrSweptHit: actual Movement compacts height 4 to 1;
the unchanged target remains height 4. Independent waist centers .5 and 2 miss without offset and align with +1.5,
through both final-point and swept candidate/narrowphase paths, HP, ledger and feedback.

Added GPU test OffscreenAirborneBodyStillDrawsItsGroundProjectionInOneSharedBatch: both sprite cards are offscreen,
but a visible floor must darken under the independently bounded shadow, remain unchanged outside the disk, use one
shadow submission, and lose the shadow after the arena lease is released. This test is authored, NOT RUN.

Native container lifetime/scheduling review follows Unity's [NativeContainer safety documentation](https://docs.unity.com/en-us/engine/6000.3/manual/scripting/optimization/job-system/thread-safe-types/native-container).

## Verification and open acceptance

Run Tools/Verify-Stage05Ability.py and Tools/Verify-Stage05Feedback.py with tree-sitter-c-sharp installed.
Both check source syntax and independent numerical models; neither compiles C#, runs jobs, or validates shaders.
The follow-up evidence is recorded separately in Evidence/74/review-source-model.json; earlier evidence is retained.

Unity compilation, EditMode/PlayMode (including all new regressions), Burst execution, GPU readback, CPU/GPU/GC
benchmarks, and owner visual/manual acceptance are NOT RUN. No Unity executable or .NET compiler is available here.
The existing regression tests are evidence of the intended contract, not evidence of a completed test run.

In particular, the weak/strong O example against the same free group, conservative corner holds, 16-pass cost at 400
actors, and shadow appearance/occlusion on real ramps/platforms still require Unity measurements. Source review cannot
establish those acceptance requirements. No previous-stage performance or graphics result is reused.
