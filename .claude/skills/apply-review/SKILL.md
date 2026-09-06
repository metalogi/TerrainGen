---
name: apply-review
description: Apply the code-review findings the user has approved to a pull request branch. Use after /review-prs has posted findings and the user wants some or all of them fixed — "apply the review", "fix findings 1 and 3", "address the review on PR 4". Presents findings for approval first, then pushes the fixes to the PR branch. Never merges.
---

# Apply approved review findings

The review loop reports; this applies. The gate between them is the user choosing
which findings to act on — that choice is the whole point of the workflow, so
never skip it.

## Steps

1. **Identify the PR.** Take the number from the user. If they did not give one,
   list open PRs and ask which.

2. **Load the findings** from the newest `.claude/review/pr-<number>-*.md`. If the
   PR has been reviewed more than once, use the most recent file and say which
   revision it covers. If no findings file exists, tell the user to run
   `/review-prs` first rather than reviewing from scratch here.

3. **Present the findings as a numbered list** — one line each, most severe first —
   and ask which to apply. Accept "all", a subset, or "none". Do not start editing
   before the user answers; this is the approval gate.

4. **Check out the PR branch:**

   ```bash
   gh pr checkout <number>
   ```

   Confirm with `git rev-parse --abbrev-ref HEAD` that you are on the PR branch and
   not on `main`.

5. **Apply only the approved findings.** Fix exactly what was approved. If fixing
   one reveals a further problem, mention it — do not quietly widen the change, and
   do not fix findings the user declined.

6. **Commit** with a message naming the findings addressed, ending with the
   `Co-Authored-By` trailer this project uses.

7. **Show the diff and confirm once**, then push to the PR branch:

   ```bash
   git push
   ```

   Never force-push a branch that already carries review comments.

8. **Report** what was applied, what was skipped and why, and the PR URL. Leave the
   PR in draft.

## Hard rules

- **Never merge**, and never mark the PR ready for review — the user does both.
- **Never apply a finding the user did not approve**, however obviously correct it
  looks. Say what you noticed instead.
- Pushing new commits invalidates the last review: the next `/review-prs` pass will
  see a new head sha and review the fixes. That is intended — do not suppress it.
