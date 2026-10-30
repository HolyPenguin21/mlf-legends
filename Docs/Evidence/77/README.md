# #77 — available evidence and remaining verification

Tested clean source commit: **`a48efa2ba2e5cfef6cdb0be5b00e212ee6fea1e2`**, tree
`70ce0a91b6f1052cae31eed82c143ae1e5b0f29b`. Base main: `d5e97dea7326fa6bc6ccd28ebb44f823c601a92d`.
The subsequent evidence commit changes only this directory; Assets/Tools are identical to the tested source.

| Check actually performed | Result | Raw evidence |
| --- | --- | --- |
| Existing #73 SOURCE syntax + independent impulse/physics equations, 10,000 cases | PASS (not C#/Jobs execution) | [ability-source-model.json](ability-source-model.json) |
| Existing #74 SOURCE syntax/schema + independent nearest/waist/fade/projection equations, 10,000 cases | PASS (not runtime/GPU) | [feedback-source-model.json](feedback-source-model.json) |
| #77 SOURCE/data: 121 C# files parse; metadata; one accepted command/facing path; historical contact data; VFX-only consumer; shader layout/policy | PASS (not compilation) | [effects-source-data-model.json](effects-source-data-model.json) |
| #77 input/asset preservation: old bindings, all pre-existing owner config/scene fields, character manifest, Action/Movement/contact solver/body+shadow shaders unchanged | PASS | Same report; full file SHA256 manifest |
| #77 independent exact-rational damage interval / relative contact / normalized frame shares, 10,000 cases; bounded ring, 10,000 operations | PASS (arithmetic models only) | Same report |
| Complete diff whitespace check against base | PASS | Required by Verify-Stage05Effects.py |
| Unity import/compilation, EditMode/PlayMode/Burst/Graphics/Performance | **NOT RUN** | [environment.json](environment.json) |
| Actual GPU milliseconds, actual warm CPU/GC/native/GPU deltas, owner visual/manual acceptance | **NOT RUN / NOT MEASURED** | No substitute measurements supplied |

Source fingerprint (Assets and Tools, excluding evidence):
`9bdb7d5d4ab873e71975811ce216348a1799f58809d940d2bf13455cd9c4cd6c`.
No old 603/603 or 59/59 result is reused. Authored Unity tests are **AUTHORED / NOT RUN**.

## Bottom-up source review

Reviewed configuration/validation/cache revision → frozen Timing/Strike → accepted F input/mode/locks → sole Facing
and LaunchTick0 → accepted Movement/Height/path and final index → clipped query/sweep/waist/occlusion → ledger/HP/impulse
confirmation → numerical event history → historical frame/anchor/flip/layer → batched buffers/shader/bounds → pause and
End/Restart/despawn/World/disable cleanup. This review is source inspection, not independent runtime acceptance.

Corrections made while implementing:
- Damage eligibility originally depended on PhaseAt==Active; use the one frozen interval for Hit, clipped bounds/sweep,
  gizmo and independent point oracle, retaining graphic/movement/chain/launch timing.
- Guard repeated same-tick Swing publication; sort starts by attacker ID before bounded history publication.
- Inspector cache signature omitted Combat-only edits, making the new read-only interval stale; include Combat and VFX
  catalog revisions, with an authored regression. Runtime effect-revision polling uses indexed arrays, no enumerator allocation.
- Technical atlas now satisfies the existing parser's 2px extrusion/trim/non-overlap contract. The data verifier enforces it.
- Effect sorting uses reused native storage. Detached Impact keeps its historical point/time and fixed bounded depth budget;
  it does not inherit the moved target's current position or depth group. No speculative impacts after a failed Hit job.
- New metadata trailing whitespace was removed; the final check covers the full base-to-head diff, not only unstaged files.

No independently confirmed base compilation/runtime blocker was found without Unity. Regression retention includes the
ordinary Hit's constructed empty NativeArray, exact/velocity-only contact budget, Frozen and shared slice budget fixes.

## Reproduce on a Unity machine

See [STAGE05_3.md](../../STAGE05_3.md) for exact settings, O examples, ownership, caps and the manual protocol.

1. Import/compile with the repository versions and Burst ON. Run the existing #73/#74 suites before interpreting new VFX.
2. Run EditMode CombatStrikeEffectsTests, CombatFacingTests, CombatFeedbackTests, CombatPhysicalAbilityTests,
   PlayerInputProducerTests, replay and their existing regression dependencies. New tests cover Damage0/HP0, long O into
   Recovery, interval boundaries, once-per-target impulses, mode/source acceptance, current strike/landing locks, same-tick
   launch, pause/lifecycle, capacity, historical contact position, Inspector invalidation and binding freeze.
3. Run PlayMode/Graphics CombatStrikeEffectsGraphicsTests without -nographics. Its readback compares enabled/disabled
   technical effects on the same paused numerical state, separate clips/pages, repeat Refresh and re-enable/expiry.
4. Run Performance CombatSpritePerformanceTests.StrikeEffectsCost2_100_400: ordinary/mass, VFX OFF/ON with identical
   commands/settings, separate warm-up, raw full tick/Refresh/body/effects/total CPU/host-frame time, positive/empty allocation
   controls, actual Burst markers, accepted-hit counts, native scratch/frame and GPU frame/instance capacity. TSVs are under
   TestResults/performance. GPU milliseconds are not measured by this harness.
5. Owner smoke: weak/strong O against the same free group, then a wall/trapped group; different victims, displacement,
   holds/discards and historical flash points. Check miss, multi-target, F retreat/Z/landing/U/I/O/LaunchTick0, both facings,
   anchor+fallback, front/back/wall/airborne/offscreen/page changes, tint/shadows and lifecycle.

Remaining limits: Unity/C#/Jobs/actual Burst/GPU safety and appearance are unverified; the source/model PASS above cannot
certify them. The World cache has 640 binding slots (64 complete revisions); overflow disables only new VFX with warning/
counters while valid gameplay settings still publish. History/instance overflow follows the stable documented presentation
policy. Stage04/05 owner acceptance remains open. One PR, merge only by separate owner command.
