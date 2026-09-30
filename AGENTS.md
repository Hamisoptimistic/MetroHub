# AGENTS.md: read this first

You are changing MetroHub, a Windows WPF launcher. The owner verifies your work by hand, so mistakes cost real time. Work carefully and stay small.

## Hard rules

1. **Never launch MetroHub.** Do not start the app. When a task needs the built app, publish it to the Desktop using the publish method this repo already uses (check `tools/` and `docs/`; if you can't find it, ask). The owner starts it and tests it.
2. **Never work on `main`.** Start each task from a clean tree on `main`, then create the task's branch (for example `refactor/T-22-hubstate`). Commit only on that branch. Never merge, rebase, force-push, delete branches or rewrite history. Do not push unless the owner asks. The owner merges.
3. **Do exactly one task per session**, from `docs/dev/MIGRATION_TRACKER.md` (or `.agents/Refactor/MIGRATION_TRACKER.md`): the first `TODO` whose dependencies are `DONE`.
4. **Verify before you claim.** Open the code before you reference it. Never invent a file, type, method, package or command. If it isn't found, say so and ask.
5. **No changes outside the task.** No drive-by fixes, renames, reformatting. Log anything else under *Found, not fixed* in the tracker.
6. **Prove it:** paste the `dotnet build` and `dotnet test` result lines, `git branch --show-current`, the `git diff --stat`, and the grep output that shows your cleanup is complete.
7. **Delete what you replace, in the same commit.** No commented-out code, no `Old`/`Legacy`/`V2` copies, no unused usings. Temporary bridges must be marked `// TEMP-SHIM(T-xx)` and listed in the tracker.
8. **Update the tracker before committing,** every session, including the branch name and commit hash.
9. **Stop and ask** if the working tree is dirty at the start, if `main` has moved and the branch would need conflict resolution, if tests fail and the cause isn't clear, if the task grows beyond ~5 files, if a public contract must change, or if behavior might change for the user.

## Read, in this order

1. `docs/ARCHITECTURE.md` (or `.agents/Refactor/metrohub_architecture-1.md`)
2. `docs/dev/MIGRATION_TRACKER.md` (or `.agents/Refactor/MIGRATION_TRACKER.md`)
3. `docs/WIDGET_LIFECYCLE_STANDARDS.md` (widget teardown rules), when touching widgets

## Starter prompt (owner pastes this to begin a session)

```
Read AGENTS.md, docs/ARCHITECTURE.md section 18, and docs/dev/MIGRATION_TRACKER.md.
Do exactly one task: the first TODO whose dependencies are DONE.
Work on a task branch (never on main) and never merge. Follow the task loop.
Verify everything you claim. Do not launch the app.
Do not fix anything outside the task; log it under "Found, not fixed".
Finish with the end-of-session report and update the tracker.
```
