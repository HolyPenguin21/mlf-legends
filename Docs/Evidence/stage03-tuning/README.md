# Stage 03 tuning — evidence (Inspector roster size, frame ranges, strike phases)

Tested commit **`3c9dffa`** (branch `feature/stage03-inspector-tuning`), clean detached worktree `D:/Unity/Project/mlf-w48`
(warm Library), Unity 6000.5.4f1, RTX 2060 SUPER D3D12. Each series twice. Raw NUnit XML and Editor logs: `raw/3c9dffa/`.
Later commits on the branch change only Docs/Evidence.

## Unity runs (actual execution)

| Series | Command | Round 1 | Round 2 | Status |
|---|---|---|---|---|
| EditMode (all, incl. Performance) | `-batchmode -nographics -runTests -testPlatform EditMode` | 424/424 | 424/424 | **PASS** |
| PlayMode headless | `-batchmode -nographics -runTests -testPlatform PlayMode` | 44 passed, 0 failed, 22 skipped | same | **PASS** for 44; the 22 skipped are GPU tests (`NOT RUN: no graphics device`), covered below |
| PlayMode GPU Graphics + Performance | `-batchmode -force-d3d12 -runTests -testPlatform PlayMode -testCategory "Graphics;Performance"` | 22/22 | 22/22 | **PASS** |
| Batchmode compile (`-quit`) on the first WIP commit | — | exit 0, no `error CS` | — | **PASS** |

Earlier rounds on intermediate commits (not evidence, kept here for honesty): `bdfea07`/`aa83d62` EditMode found 6 failures
(two test-data issues from the new Preparation movement and the strike-table entity count), fixed; `aa83d62`/`1b78c53`
PlayMode found (a) a bootstrap error in partial test Worlds without `CombatActionSystem` (fixed: such Worlds are skipped),
(b) one renderer test expecting the removed `strike_pose` error (now an atlas-shortening EndFrame check), (c) 3 scene tests
failing because the owner's concurrent Editor edits to `CombatSmoke.unity` (AI team size 2, empty config reference, a Cube)
had been committed by mistake — reverted in `3c9dffa`; the owner's working copy was left untouched.

## Requirement → test

| Check | Test(s) | Status |
|---|---|---|
| Start/restart with 2 / 100 / 400, counts per team, no fighters of the previous run, edits wait for a restart | PlayMode `BootstrapRosterAndStrikeWindowTests.TeamSizesSpawnOnStartAndRestart…` (1+1, 50+50, 200+200, 3+1, 1+1, disable/enable 2+98) | **PASS** |
| Invalid sizes rejected, not clamped, running battle kept | `…InvalidTeamSizesAreRejectedNotClamped…` (0, 201, −3) | **PASS** |
| StartFrame / EndFrame / derived FrameCount, out-of-range EndFrame | `SpriteAnimationTimingTests.InvalidCountDuration…`, `CombatAnimationSetTests.ProjectConfigBinds…` | **PASS** |
| One frame, full and shortened range | PlayMode run clip `EndFrame 0`; `DurationCurveAndRange…` (EndFrame 4 never shows frame 5); project full ranges | **PASS** |
| Migration keeps range, duration, curve; not shifted twice | `CharacterAnimationMigrationTests` (real YAML import/save/reimport); static asset diff below | **PASS** |
| Valid/invalid phases, EndFrame change, atlas update | `MissingOrInvalidDataIsRejected…`, PlayMode `ConfigEditsInvalidate…` (atlas clip shortened → `EndFrame: 2 is outside 0..1`) | **PASS** |
| Phases = simulated windows, drawn phase = simulated phase every tick | `ConfigPhasesAreTheSimulatedWindows…` ×5, `DurationCurveAndRange…`, `ARunningStrikeKeepsItsTiming…` | **PASS** |
| Movement in Preparation, no reset, no run frame, no jump | `CombatStrikeScheduleTests.PreparationMovement…`, `CombatAttackTests.StrikesMoveOnlyInPreparation…` | **PASS** |
| No damage before/after Active, once per target, 1-tick Active, boundaries inside one tick | `DamageResolvesOnlyInActive…` (0/1/1, 4/1/6, 2/8/3, 10/2/1), `TimingQuantizationNeverSkipsActive…` | **PASS** |
| Edits mid-strike don't change it; new strikes use the new table | `PublishValidatesTheTable…`, PlayMode `TheProjectConfigPublishes…` | **PASS** |
| Render-rate independence | `PublishedWindowsGiveTheSameBattleAtAnyRenderPacing` (2/3/7 ticks per frame), `CatchUpFrames…` | **PASS** |
| Pause, resume, restart, despawn (attacker and target) in every phase | `PauseResumeRestartAndDespawnInEveryPhase` ×3 | **PASS** |
| Short Recovery keeps the series | `AShortRecoveryStillChains…` | **PASS** |

## Static checks (not Unity execution)

| Check | Status |
|---|---|
| No PNG / art source in the branch diff | **PASS** |
| No duplicate `.meta` GUIDs | **PASS** |
| Config asset: only range/phase fields changed; every `TotalDurationSeconds`/curve identical to main; `FrameCount N → EndFrame N−1` for all 8 clips | **PASS** |
| Scene diff = 4 bootstrap lines only; atlas `.meta` identical to main | **PASS** |

## NOT RUN

Manual Inspector/Game-view tuning by the owner (final Stage 03 check), real keyboard play, Domain Reload disabled,
player build, Frame Debugger, Burst-off series. Stage 03 is **not** declared accepted.
