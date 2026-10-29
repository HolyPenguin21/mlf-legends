# #66 evidence — Unity runs, 2026-10-29

Machine: AMD Ryzen 5 3600 (12 logical), 32 GB, RTX 2060 SUPER (D3D12 for PlayMode), Windows 11 26100.
Toolchain: Unity 6000.5.4f1, Entities/Collections 6.5.0, Burst 1.8.29, URP 17.5.0, safety checks on.
Runs in a detached worktree (the owner's Editor holds the main project) on the files committed on
`feature/66-impulses-indexed-hit`. Results only from actual runs: **PASS / FAIL / NOT RUN**.

| Check | Scope / commit | Result |
|---|---|---|
| Compile, clean base `cf2b5bc` | C# | **FAIL**: CS0246 `CombatReplayDriver.cs:619` (test assembly), fixed in `8568f0a` |
| EditMode non-Performance, base + compile fix | `cf2b5bc` + using | **FAIL 523/527**: 3 arena SendMessage asserts, 1 sprite crossing fixture |
| EditMode non-Performance | `8568f0a` | **PASS 527/527** |
| EditMode non-Performance | branch incl. solver convergence fix | **PASS 567/567** (CombatImpulseTests 20, CombatIndexedHitTests 16, CombatBodyContactTests 39 incl. 4 pressing-front cases) |
| PlayMode (GPU) | base `8568f0a` | **FAIL 48/66** (18) |
| PlayMode (GPU) | branch incl. `c1a1c0a` and the solver fix (identical before it) | **FAIL 56/66**: the 10 `PlayerInputPlayModeTests` fail only in the full ordered run ("CombatSmoke requires CombatArenaSystem"); one of them alone **PASS**; same tests failed on the base. Pre-existing order dependency, not fixed |
| Performance `BodyContactScenarios2_100_400…`, Burst on | branch incl. solver fix | **PASS** (GC 0 in owner loops, indexed = brute-force Inside pairs, Active strikes and impulses present, no whole-island rollback in pressing fronts). [TSV](stage04-impulses-hit-burst-on.tsv) |
| Burst-off benchmark | — | **Excluded** by owner decision (2026-10-29): production code runs with Burst; the managed run is not a project check |
| Other Performance suites (baseline, arena, strikes, sprites) | — | **NOT RUN** on this branch |
| Manual Unity checklist (STAGE04_IMPULSES_HIT §7) | — | **NOT RUN** (owner) |

## Pressing-front convergence fix — before / after (Burst on, µs mean, 600 warm ticks)

Before = this branch before the fix (also the 04.3 behaviour for the no-impulse rows). Frozen = body-ticks held or
rolled back; after the fix no scenario has a whole-island rollback.

| bodies / scenario | Movement before → after | full tick after (mean / p95) | frozen before → after | notes after |
|---|---:|---:|---:|---|
| 100 opponents | 1393 → 486 | 668 / 925 | 0 → 0 | 3310 iterations / 600 ticks |
| 100 impulse_single | 2897 → 1068 | 414 / 523 | 60000 (all, every tick) → 443 holds | 1036 slop pairs, max .0149 |
| 100 impulse_mass | 3036 → 583 | 466 / 589 | 58320 → 7 holds | |
| 400 opponents | 15463 → 2021 | 3077 / 4339 | 240000 (all) → 0 | Hit 198, accepted hits 400 |
| 400 strikes | 16068 → 1868 | 2721 / 3867 | 240000 → 0 | accepted hits 800 |
| 400 impulse_single | 15771 → 9247 | 4666 / 7161 | 240000 → 1757 holds | |
| 400 impulse_mass | 15867 → 2612 | 3322 / 3997 | 240000 → 8000 holds | every impulse tick holds all 400 (impulses discarded, counted) |
| 400 corridor | 5151 → 13154 | 13905 / 15903 | 240000 → 37800 holds | single-file column: 63 bodies held per tick, all iterations used |

Managed-kernel flags (contacts and Hit) were False: Burst executed.

## Findings for the manager

1. **Fixed: dense pressing fronts no longer freeze.** Causes found by a diagnostic probe: (a) two uncontrolled allies
   shared a press by mass, so a rear row kept shoving a front row held by the enemy and convergence halved per row
   (400 front never converged in 48 iterations); (b) a leftover swept violation of ~.005 rolled back the whole island.
   Corrections: uncontrolled ally pairs give the correction to the body whose own command presses in (mass if none);
   leftover ≤ 5 % of the smaller radius is published (resolved next tick as initial overlap, enemy pressers own it so
   there is no slow bulldozing); remaining violators are held at their start for this tick (bounded rounds + closure)
   instead of rolling back the island. Regression: `PressingFrontsKeepMovingWithoutRollbackOrBulldozing` (100/400,
   with/without impulse) asserts no rollback, ≤20 % held per tick, no front drift and a working retreat.
2. **Remaining limits:** a mass-impulse tick into a 400 jam holds all bodies for that tick (impulses discarded and
   counted); a 400 single-file corridor keeps 63 bodies held per tick and uses the full iteration budget (13 ms).
3. **Indexed Hit search is not cheaper in dense packs.** Same snapshot, same pairs: faster than the full scan in sparse 400
   (347 vs 785 µs), ~1.7× slower in dense 400 (≈1380 vs ≈800 µs): managed batch query cost, not the Burst kernel.
4. **Benchmark fixture correction:** the 04.3 contact benchmark started the battle before committing the arena, so
   frozen bodies kept an airborne spawn state and never struck; fixed by committing geometry first.
