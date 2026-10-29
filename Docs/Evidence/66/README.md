# #66 evidence — Unity runs, 2026-10-29

Machine: AMD Ryzen 5 3600 (12 logical), 32 GB, RTX 2060 SUPER (D3D12 for PlayMode), Windows 11 26100.
Toolchain: Unity 6000.5.4f1, Entities/Collections 6.5.0, Burst 1.8.29, URP 17.5.0, safety checks on.
Runs in a detached worktree (the owner's Editor holds the main project); the worktree's tests ran against the files
committed on `feature/66-impulses-indexed-hit` plus, for the benchmark, the final benchmark file of the docs commit.
Results only from actual runs: **PASS / FAIL / NOT RUN**.

| Check | Scope / commit | Result |
|---|---|---|
| Compile, clean base `cf2b5bc` | C# | **FAIL**: CS0246 `CombatReplayDriver.cs:619` (test assembly), fixed in `8568f0a` |
| EditMode non-Performance, base + compile fix | `cf2b5bc` + using | **FAIL 523/527**: 3 arena SendMessage asserts, 1 sprite crossing fixture |
| EditMode non-Performance | `8568f0a` | **PASS 527/527** |
| EditMode non-Performance | full branch (physics + Hit) | **PASS 563/563** (CombatImpulseTests 20, CombatIndexedHitTests 16, contacts 35, attacks 31, spatial lifecycle 26) |
| PlayMode (GPU) | base `8568f0a` | **FAIL 48/66** (18) |
| PlayMode (GPU) | full branch with `c1a1c0a` | **FAIL 56/66**: the 10 `PlayerInputPlayModeTests` fail only in the full ordered run ("CombatSmoke requires CombatArenaSystem"); one of them run alone **PASS**; same tests failed on the base. Pre-existing order dependency, not fixed |
| Performance `BodyContactScenarios2_100_400…`, Burst on | full branch | **PASS** (GC samples 0 in every owner loop, indexed = brute-force Inside pairs, Active strikes/impulses present). [TSV](stage04-impulses-hit-burst-on.tsv) |
| Same, `--burst-disable-compilation` | full branch | **FAIL (timeout only)**: all 24 scenarios measured and the [TSV](stage04-impulses-hit-burst-off.tsv) written (managed-kernel flags True, GC samples 0, search match True), but the run took 2673 s > the 1800 s test timeout |
| Other Performance suites (baseline, arena, strikes, sprites) | — | **NOT RUN** on this branch (an early full-suite base run was stopped) |
| Manual Unity checklist (STAGE04_IMPULSES_HIT §7) | — | **NOT RUN** (owner) |

## Burst-on measurements (µs, mean over 600 warm ticks; overlapping, not additive)

| bodies / scenario | Movement | kernel | index | Hit | full tick mean / p95 | frozen bodies·ticks | Active ticks / accepted hits | search brute / indexed |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 2 strikes | 28 | 10 | 6.5 | 9.3 | 75 / 102 | 0 | 100 / 40 | 5.6 / 5.7 |
| 100 opponents | 1393 | 1216 | 81 | 52 | 1589 / 1717 | 0 | 50 / 400 | 87 / 317 |
| 100 strikes | 1270 | 1090 | 80 | 81 | 1490 / 1702 | 0 | 100 / 800 | 85 / 316 |
| 100 impulse_single | 2897 | 2744 | 84 | 56 | 3132 / 3555 | **60000 (all, every tick)** | 50 / 400 | 83 / 329 |
| 100 impulse_mass | 3036 | 2882 | 85 | 54 | 3236 / 4115 | 58320 | 50 / 380 | 86 / 311 |
| 400 sparse | 994 | 227 | 384 | 107 | 1635 / 1981 | 0 | 50 / 0 | 790 / 320 |
| 400 opponents | 15463 | 14785 | 379 | 201 | 16321 / 18802 | **240000 (all)** | 50 / 400 | 811 / 1384 |
| 400 strikes | 16068 | 15389 | 386 | 328 | 17129 / 19941 | 240000 | 100 / 800 | 815 / 1353 |
| 400 impulse_mass | 15867 | 15190 | 384 | 205 | 16737 / 20312 | 240000 | 50 / 400 | 812 / 1336 |

Managed-kernel flags (contacts and Hit) were False: Burst executed. Burst off, same scenarios (µs, Movement / full tick): 2 strikes 65 / 122; 100 strikes 13768 / 14072; 400 sparse 4623 / 5519; 400 strikes 195429 / 196237 — the contact kernel is ~12× slower managed; index (~400 µs at 400) and Hit (~230–376 µs) barely change.

## Findings for the manager

1. **Dense pressing fronts freeze.** With the opposing-front layout (rows exactly touching, both teams walking in),
   all 400 bodies are rolled back every tick even without impulses (04.3 behaviour, now measured for the first time).
   At 100 the same front is solved, but after one impulse into it the island is unresolved and frozen on every later
   tick; impulses there are discarded (counted). Movement then costs ~15 ms/tick at 400 because every tick exhausts the
   48 iterations. The bounded fallback is diagnosed as specified, but this is not acceptable gameplay for 400 and needs a
   solver decision (iteration/convergence or partial-island rollback) before Stage 04 closes.
2. **Indexed Hit search is not cheaper in dense packs.** Same snapshot, same pairs: indexed beats the full scan in
   sparse 400 (320 vs 790 µs) but is ~1.7× slower in dense 400 (≈1350 vs ≈810 µs). The cost is the managed batch query
   (membership validation, per-range sort, many duplicate cell entries), not the Burst Inside kernel.
3. **Benchmark fixture correction:** the 04.3 contact benchmark started the battle before committing the arena, so
   frozen bodies kept an airborne spawn state and never struck; fixed by committing geometry first.
