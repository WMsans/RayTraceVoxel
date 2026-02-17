---
name: dev-log-scriptwriter
description: Generate entertaining YouTube dev log video scripts from a git repository's commit history. Scans hundreds of commits, identifies technically interesting or visually funny moments, and writes a casual, engaging narration script suitable for non-technical viewers. Use when the user asks to (1) create a dev log video script from a repo, (2) write a YouTube video script about a project's development history, (3) summarize a repo's commit history as a video narrative, (4) generate a dev log or devlog script, or (5) turn git history into a story.
---

# Dev Log Scriptwriter

Generate a fun, casual YouTube dev log script (5-15 min) from a git repo's commit history. Handles repos with 400+ commits and large diffs through batch processing.

## Workflow

### Step 1: Scan Commits

Run the scanning script to extract and score commits:

```bash
python scripts/scan_commits.py --repo <path> --output commits_scan.json
```

Options:
- `--batch-size N` — commits per batch (default 50; lower if diffs are huge)
- `--max-diff-lines N` — truncate diffs per commit (default 200)
- `--since DATE` / `--until DATE` — filter by date range
- `--branch NAME` — specific branch (default: HEAD)
- `--min-score N` — interestingness threshold (default 5; raise to narrow results)

The script outputs `commits_scan.json` with:
- `interesting_commits` — scored and tagged commits, sorted by score descending
- `timeline_summary` — commits grouped by month with tag counts
- `total_commits` / `interesting_count` — totals for context

### Step 2: Select Commits for the Script

Read `commits_scan.json`. From `interesting_commits`, select 15-25 commits that form a narrative arc:

1. **Must include:** highest-scored commits, the `origin` commit, any `launch`/`release` commits
2. **Prioritize:** commits tagged `crash`, `frustration`, `hack`, `visual_bug`, `classic_bug`, `nasty_bug` — these make the best stories
3. **Group by timeline:** use `timeline_summary` to identify development phases for the act structure
4. **Balance:** mix technical breakthroughs with funny bugs; avoid back-to-back commits of the same tag type

If the scan returns too few interesting commits (< 10), re-run with `--min-score 3`.
If too many (> 50), re-run with `--min-score 8` or select manually from the top.

### Step 3: Review Diffs of Selected Commits

For selected commits where `diff_snippet` is empty or insufficient, fetch fuller context:

```bash
git -C <repo> show <full_hash> --stat
git -C <repo> show <full_hash> -p
```

Read just enough to understand what changed and why. Do not load entire diffs into context — read the stat first, then targeted file diffs.

### Step 4: Write the Script

Read [references/script-structure.md](references/script-structure.md) for the full script format, pacing guide, storytelling beats, and diff-to-narration translation rules.

Key points:
- **Tone:** Casual, self-deprecating, entertaining. Like a friend telling you about their project over beers.
- **Audience:** Non-technical viewers should follow along. Explain concepts via analogy, not jargon.
- **Format:** Narration only (no visual cues). Structured as Cold Open > Intro > Acts > Outro.
- **Pacing:** 20-40 seconds of narration per commit. Never more than 3 commits without a joke or transition.

Output the script as a single markdown file.

### Step 5: Present and Iterate

Present the draft script. Common adjustments:
- Reorder or cut commits that don't flow well
- Adjust tone (more/less technical, more/less humor)
- Expand a particular bug story or breakthrough
- Add/remove acts based on target video length
