# Issue #22 / PR #37 — raw Unity test evidence

Raw files exactly as Unity and the benchmark wrote them (NUnit XML, Editor.log, TSV), committed at the owner's request
so the manager can review them without the agent's machine. Recorded by the implementing agent; the manager has not
re-run Unity. Machine: Windows 11 Pro 10.0.26100, AMD Ryzen 5 3600, 32 GB. Unity 6000.5.4f1, Entities/Collections
6.5.0, Burst 1.8.29, URP 17.5.0, Input System 1.19.0. Clean detached checkout of the named commit in the ASCII worktree
`D:/Unity/Project/mlf-s7` (warm Library), `Tools/Run-UnityTests.ps1` (batchmode `-nographics`), one Unity process per run.
Logs contain local paths and the Windows user name in licensing channel names; no serial/licence IDs or e-mail.

| Folder | Commit | Content | Result |
|---|---|---|---|
| `4d9e431/` | `4d9e431bc7474cf7233f8edaec60f0c14df65ab0` (after the manager review; code = `4768386`) | EditMode ×2, PlayMode ×2, Performance Burst off; Burst-on TSVs from the two EditMode runs | **EditMode 244/244, 244/244; PlayMode 21/21, 21/21; Performance (Burst off) 7/7** |
| `4768386-negative-control/` | `4768386` + `mutations.diff` (uncommitted, temporary) | only the 8 review regressions, run against four mutations that restore the old behaviour (End resets pan; Attach unbinds; adapter binds on enable only; no blocking of restored holds) | **EditMode 0/4, PlayMode 0/4 (all FAIL, as intended)** — the regressions detect the reviewed defects |
| `d879d4c/` | `d879d4cb47917bb72ed4d48a056487314a1818e3` (first PR revision, before the review) | same series | EditMode 239/239 ×2; PlayMode 18/18 ×2; Performance (Burst off) 7/7 — superseded, the reviewed defects were not covered |
| `1408887-failed/` | `1408887c08b53728ef7ab6d7c147e07390044a96` | same series | **FAIL**: EditMode 237/239 ×2, Performance (Burst off) 6/7; PlayMode 18/18 ×2 (two new tests; fixed in `141df7d`/`d879d4c`) |

`burst-on-runN/` hold the TSVs written by the Performance tests inside EditMode run N (Burst code executed); `burst-off/`
those of the `--burst-disable-compilation` run (Burst code not executed). `stage02-33-*` = existing Stage 02.2 benchmark,
`player-input-*` = the #22 producer benchmark (Docs/PERFORMANCE_BASELINE.md).
