---
name: ship
description: Finish a piece of work by opening a pull request instead of landing it on main. Use when a task is complete and ready for review, or when the user says "ship it", "open a PR", "submit this", "raise a PR". Creates a feature branch if needed, commits, pushes, and opens a draft PR on metalogi/TerrainGen. Never merges.
---

# Ship: open a PR for review

Work reaches `main` through a reviewed pull request, never through a direct push.
`.claude/hooks/guard-main.pl` enforces this at the harness level — if you find
yourself blocked by it, you skipped a step below.

## Preconditions

- `gh` is installed but is **not on PATH** in the agent's shell. Put it there first,
  or step 6 fails with `gh: command not found`:

  ```bash
  command -v gh >/dev/null || export PATH="/c/Program Files/GitHub CLI:$PATH"
  ```

  The PowerShell tool needs its own equivalent — the line above does nothing there:

  ```powershell
  if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { $env:PATH = "C:\Program Files\GitHub CLI;$env:PATH" }
  ```

- `gh` must be authenticated (`gh auth status`). If it is not, stop and tell the
  user to run `gh auth login` — never handle their credentials yourself.
- `GH_REPO=metalogi/TerrainGen` is pinned in `.claude/settings.json` so repository
  resolution is deterministic. (`gh` also resolves the repo correctly on its own
  through the `github-personal` SSH alias — the pin is belt-and-braces, not a fix.)

## Steps

1. **Get onto a feature branch.**
   Check `git rev-parse --abbrev-ref HEAD`. If it is `main`, create a branch named
   for the work — `m1-height-jobs`, `fix-seam-stitch-order` — and move the changes
   onto it. Never commit on `main`.

2. **Look at what you are about to commit.**
   Run `git status` and `git diff`. Leave out anything incidental: `Library/`,
   `Temp/`, `obj/`, `.vs/`, `Logs/`, and any scene or asset file you changed only
   to test something. Unity `.meta` files must be committed alongside the assets
   they belong to — an asset without its `.meta` breaks the project for everyone else.

3. **Check the code compiles.** This project has no CLI build. If Unity is open,
   ask the user to confirm the Editor console is clean; if it is not open, say
   plainly in the PR body that the change is unverified rather than implying it built.

4. **Commit** in logical units with a message explaining *why*, ending with the
   `Co-Authored-By` trailer this project uses.

5. **Show the user the plan and get one confirmation** before anything leaves the
   machine: the branch name, the commit subjects, and the PR title and body you
   intend to open. Pushing and opening a PR are outward-facing; confirm once here,
   then proceed without further prompting.

6. **Push and open a draft PR:**

   ```bash
   git push -u origin <branch>
   ```

   ```bash
   gh pr create --draft --head <branch> --base main --title "<title>" --body "<body>"
   ```

   Always `--draft`, and always pass `--head` explicitly — an explicit `GH_REPO`
   overrides repository detection, which is where head-branch inference gets shaky.

7. **Report the PR URL** and stop.

## PR body shape

Use these four sections:

- **What** — one paragraph: what changed and why.
- **Milestone** — which of M0–M7 in `SonomaRevisedPlan.md` this serves, or "none".
- **Verification** — what you actually ran or checked. If nothing, say so explicitly.
- **Review notes** — anything you are unsure about, or deliberately left for later.

End the body with the Claude Code attribution line this project uses on PRs.

## Hard rules

- **Never merge.** Not `gh pr merge`, not a local merge into `main`, not a push to
  `main`. The reviewer decides when a PR lands. The hook will block you anyway.
- **Never mark a PR ready for review yourself** — leave it draft until the review
  loop has run and the user has acted on the findings.
- **Never force-push** over a branch that already has review comments on it.
