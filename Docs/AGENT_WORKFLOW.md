# Agent workflow — MLF Legends

## Manager / developer division
**Project owner** approves major scope changes and delegates GitHub Issues to developers or AI agents.

**Technical manager (ChatGPT)** is responsible for:
1. Tracking roadmap milestones against the actual `main` branch, not against verbal claims alone.
2. Authoring small, testable GitHub Issues with clear scope, dependencies and acceptance criteria.
3. Assigning integration order and preventing two agents from modifying the same runtime owner concurrently.
4. Reviewing PR files, callsites, lifecycle/cleanup, competing system writers, package compatibility, regression risks and testing evidence.
5. Requesting correction when code violates the accepted architecture; logging known risks and updating `Docs/ROADMAP.md` / `Docs/QA.md`.
6. Merging and pushing only when the project owner authorizes it; no silent approvals of material architecture changes.

**Implementing agent**:
1. Read the Issue plus `Docs/ARCHITECTURE.md`, `Docs/COMBAT_SPEC.md`, `Docs/DECISIONS.md`.
2. Pull fresh `main`, inspect code, existing structures, files, references and system writers before changing anything.
3. Implement **only the specified scope**, in a dedicated `feature/stage01-<task-number>` branch with Unity `.meta` files.
4. Perform available static checks, unit tests, lifecycle review and check error paths; runtime Unity tests can be `NOT RUN` if unavailable, **without blocking delivery**.
5. Open **one PR per Issue**, targeting `main`. The agent does not merge their own PR.
6. Report a concise list of changed files, new or removed systems, tests actually run (or NOT RUN), known risks, and PR link.

## Managing parallel nonblocking follow-ups

The technical manager must not leave discovered corrective work as informal remarks. Record each nonblocking fix in GitHub Issues with a precise owner, scope, regression checks and dependencies. The manager chooses when to dispatch it to an agent instead of asking the project owner to manually schedule each follow-up.

- **Before a consuming task:** fix if it affects correctness, lifecycle ownership, test trustworthiness, or the measurements of a subsequent step.
- **At the stage's stabilization window:** group narrow follow-ups after main implementation and before final integration/replay/performance checks, with separate PRs.
- **End of stage:** defer cosmetic/low-impact cleanup not required by downstream work, while keeping open issues visible.
- Avoid contradictory parallel edits to the same files. Only the owner-approved integration sequence determines what is ready; passing unavailable tests is never assumed.

**Current Stage 01 dispatch order:** #5 main bootstrap **MERGED** → #12 Native Temp handling → #10 Unity test runner → #14 battle lifecycle safe End/atomic restart → #6 replay/integration tests → #7 performance baseline. #6 and #7 may run in parallel after these fixes, if agents keep distinct file ownership. Stage 01.4 risk checks and decisions are tracked in #14, not folded into unrelated tasks.

## Stage 01 integration graph

```text
Issue #2 — 01.1: data contracts, runtime assembly
  └─ Issue #3 — 01.2: simulation clock / fixed tick
       └─ Issue #4 — 01.3: canonical player+AI command application
            └─ Issue #5 — 01.4: bootstrap 2 combatants, spawn/despawn
                 ├─ Issue #6 — 01.5: reproducibility and behavior tests
                 └─ Issue #7 — 01.6: 400-lite-actor profiling harness
```

Do **not** implement dependent runtime code against a guessed Issue #2 contract. #6 and #7 are parallelizable only after #5 is integrated. Any discovered contract gap is raised to the manager and fixed at the contract owner, not patched with duplicate implementation.

## Protected scopes for parallel agents

| Issue | Primary source ownership | Must not change without coordination |
|---|---|---|
| #2 | `Assets/MLFLegends/Combat/Runtime/Core/` + runtime asmdef | Clock, command dispatch, spawn systems |
| #3 | `Assets/MLFLegends/Combat/Runtime/Simulation/` | Core contracts, command producers |
| #4 | `Assets/MLFLegends/Combat/Runtime/Commands/` | Core contracts, scene / spawner |
| #5 | `Assets/MLFLegends/Combat/Runtime/Bootstrap/` and optional smoke scene | Command processing and unrelated scenes |
| #6 | `Assets/Tests/EditMode/Combat/`, `Assets/Tests/PlayMode/Combat/` | Performance harness, original smoke tests |
| #7 | `Assets/Tests/EditMode/Performance/`, `Docs/PERFORMANCE_BASELINE.md` | Test code from #6 or alternative ECS runtime |

If an Issue needs changes outside its declared directories, the agent must describe the reason in its PR and raise the overlap before editing.

## Agent handoff
Send the agent this instruction:

> Выполни только задачу из указанного GitHub Issue проекта `HolyPenguin21/mlf-legends`. Перед изменениями проверь актуальный `main`, связанные компоненты и Docs. Не добавляй второй источник истины, отдельный бой для дуэли или необоснованные новые слои. Сохраняй Unity `.meta`. Сделай отдельную ветку, коммиты и PR в `main`, но не мержи. В отчёте укажи изменённые файлы, результаты фактически выполненных проверок, `NOT RUN` для недоступных проверок Unity, известные риски и ссылку на PR. При неясности технического контракта останови именно спорную часть, а не выдумывай альтернативную архитектуру.

## Review gates
1. **Scope gate**: only Issue scope and documented adjacent dependencies.
2. **Architecture gate**: single authoritative writers, ownership clarity, no lifecycle leaks, no hidden main-thread game-loop duplicates.
3. **Correctness gate**: data flow and state transitions, edge cases (stale commands, despawn), stable order, testability.
4. **Integration gate**: current `main` can accept the PR without conflicting definitions, asmdefs or Unity GUIDs.
5. **Evidence gate**: recorded static checks and tests; Unity runtime `NOT RUN` is a tracked risk, **not** a delivery blocker.
6. **Delivery gate**: after owner authorization, merge PR, verify head in `main`, update roadmap/QA and unblock dependent Issue.

## Status
An Issue is **READY**, **BLOCKED BY DEPENDENCY**, **IN PROGRESS**, **IN REVIEW**, **MERGED / UNVERIFIED**, or **VERIFIED**. PR merge and test pass are not synonymous. No progress is assumed between chats unless confirmed through GitHub.
