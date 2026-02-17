#!/usr/bin/env python3
"""
Scan a git repo's commit history and extract potentially interesting commits
for a dev log video script.

Processes commits in batches to handle large histories (400+) and large diffs.
Outputs a JSON file with commit metadata and truncated diffs, scored by
likely "interestingness" using heuristics.

Usage:
    python scan_commits.py [options]

Options:
    --repo PATH         Path to git repo (default: current directory)
    --output PATH       Output JSON file (default: commits_scan.json)
    --batch-size N      Commits per batch (default: 50)
    --max-diff-lines N  Max diff lines to keep per commit (default: 200)
    --since DATE        Only commits after this date (e.g. 2024-01-01)
    --until DATE        Only commits before this date
    --branch NAME       Branch to scan (default: current HEAD)

Output format:
    {
      "repo": "repo-name",
      "branch": "main",
      "total_commits": 423,
      "scanned": 423,
      "interesting_commits": [ ... ],
      "timeline_summary": { ... }
    }
"""

import argparse
import json
import os
import re
import subprocess
import sys
from datetime import datetime
from pathlib import Path


# ---------------------------------------------------------------------------
# Heuristic scoring
# ---------------------------------------------------------------------------

# Commit message patterns that suggest something interesting
INTERESTING_MSG_PATTERNS = [
    (r"\bfix(?:ed|es|ing)?\b.*\bbug\b", 8, "bug_fix"),
    (r"\bcrash(?:ed|es|ing)?\b", 10, "crash"),
    (r"\bbreak(?:s|ing)?\b", 7, "breaking"),
    (r"\brevert(?:ed|s|ing)?\b", 9, "revert"),
    (r"\bhack\b|\bworkaround\b|\bkludge\b", 10, "hack"),
    (r"\bfinally\b", 6, "finally"),
    (r"\boops\b|\bwhoops\b|\bugh\b|\bfml\b|\bwtf\b", 12, "frustration"),
    (r"\b(re)?write\b|\brefactor\b|\boverhaul\b", 7, "rewrite"),
    (r"\bperformance\b|\boptimiz\b|\bspeedup\b|\bfast(?:er)?\b", 7, "performance"),
    (r"\bsecurity\b|\bvulnerab\b|\bcve\b|\bxss\b|\binjection\b", 8, "security"),
    (r"\binitial commit\b|\bfirst commit\b|\binit\b", 10, "origin"),
    (r"\bv?\d+\.\d+\.\d+\b", 6, "release"),
    (r"\blaunch\b|\bship(?:ped|s)?\b|\bdeploy\b", 7, "launch"),
    (r"\bmigrat(?:e|ion|ing)\b", 6, "migration"),
    (
        r"\b(?:visual|ui|css|style|layout)\b.*\b(?:bug|fix|broke|wrong)\b",
        9,
        "visual_bug",
    ),
    (r"\btest(?:s|ing)?\b.*\b(?:fail|broke|red)\b", 7, "test_failure"),
    (r"\b(?:off[- ]?by[- ]?one|fence[- ]?post|edge[- ]?case)\b", 9, "classic_bug"),
    (r"\b(?:typo|spelling|misspell)\b", 5, "typo"),
    (
        r"\b(?:infinite loop|stack overflow|memory leak|deadlock|race condition)\b",
        11,
        "nasty_bug",
    ),
    (r"\b(?:dark mode|theme|animation|transition)\b", 5, "visual_feature"),
    (r"\b(?:removed|deleted|killed|nuked)\b", 4, "removal"),
    (r"!{2,}", 6, "excitement"),
    (r"\?\?+", 5, "confusion"),
]

# Diff patterns that suggest something technically interesting
INTERESTING_DIFF_PATTERNS = [
    (r"(?:algorithm|recursive|memoiz|dynamic programming)", 8, "algorithm"),
    (r"(?:TODO|FIXME|HACK|XXX|TEMPORARY)", 5, "debt_marker"),
    (r"(?:async|await|promise|concurrent|parallel|thread)", 5, "concurrency"),
    (r"(?:encrypt|decrypt|hash|token|auth)", 6, "security_code"),
    (r"console\.log|print\(|debugger|binding\.pry", 4, "debug_left_in"),
]

# File extension patterns that add visual/technical interest
INTERESTING_FILE_PATTERNS = [
    (r"\.(glsl|shader|frag|vert|hlsl)$", 7, "shader"),
    (r"\.(svg|png|gif|jpg|webp|ico)$", 4, "visual_asset"),
    (r"(Dockerfile|docker-compose|\.github|\.ci)", 5, "devops"),
    (r"\.(wasm|wat)$", 8, "wasm"),
    (r"(schema|migration|seed)\.", 5, "data_model"),
]

# Boring commit patterns to penalize
BORING_MSG_PATTERNS = [
    (r"^merge\s+(branch|pull|remote)", -10),
    (r"^bump\s+version", -5),
    (r"^update\s+depend", -6),
    (r"^chore\b", -4),
    (r"^docs?\b:\s", -3),
    (r"^lint\b|\bformat(?:ting)?\b", -5),
    (r"^wip$", -4),
    (r"^auto[- ]?generat", -8),
    (r"^renovate\b|^dependabot\b|^\[bot\]", -10),
]


def run_git(args, repo_path, timeout=60):
    """Run a git command and return stdout."""
    cmd = ["git", "-C", str(repo_path)] + args
    try:
        result = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=timeout,
            encoding="utf-8",
            errors="replace",
        )
        if result.returncode != 0:
            return None
        return result.stdout
    except subprocess.TimeoutExpired:
        print(
            f"  [WARN] Git command timed out: {' '.join(args[:3])}...", file=sys.stderr
        )
        return None
    except Exception as e:
        print(f"  [WARN] Git error: {e}", file=sys.stderr)
        return None


def get_commit_list(repo_path, branch=None, since=None, until=None):
    """Get list of all commit hashes + oneline in chronological order."""
    args = ["log", "--reverse", "--format=%H|%aI|%s"]
    if branch:
        args.append(branch)
    if since:
        args.append(f"--since={since}")
    if until:
        args.append(f"--until={until}")
    output = run_git(args, repo_path, timeout=120)
    if output is None:
        return []
    commits = []
    for line in output.strip().split("\n"):
        if not line:
            continue
        parts = line.split("|", 2)
        if len(parts) == 3:
            commits.append(
                {
                    "hash": parts[0],
                    "date": parts[1],
                    "message": parts[2],
                }
            )
    return commits


def get_commit_diff_stat(repo_path, commit_hash):
    """Get diffstat for a commit (files changed, insertions, deletions)."""
    output = run_git(
        ["diff-tree", "--no-commit-id", "--numstat", "-r", commit_hash],
        repo_path,
        timeout=30,
    )
    if not output:
        return [], 0, 0
    files = []
    total_add = 0
    total_del = 0
    for line in output.strip().split("\n"):
        if not line:
            continue
        parts = line.split("\t", 2)
        if len(parts) == 3:
            add = int(parts[0]) if parts[0] != "-" else 0
            delete = int(parts[1]) if parts[1] != "-" else 0
            files.append(parts[2])
            total_add += add
            total_del += delete
    return files, total_add, total_del


def get_commit_diff_snippet(repo_path, commit_hash, max_lines=200):
    """Get a truncated diff for a commit."""
    output = run_git(
        ["diff-tree", "-p", "--no-commit-id", "-r", "--no-color", commit_hash],
        repo_path,
        timeout=30,
    )
    if not output:
        return ""
    lines = output.split("\n")
    if len(lines) > max_lines:
        return (
            "\n".join(lines[:max_lines])
            + f"\n... ({len(lines) - max_lines} more lines)"
        )
    return output


def score_commit(commit_info, files, additions, deletions, diff_snippet):
    """Score a commit for interestingness. Returns (score, list_of_tags)."""
    score = 0
    tags = []
    msg = commit_info["message"].lower()

    # Score based on commit message
    for pattern, points, tag in INTERESTING_MSG_PATTERNS:
        if re.search(pattern, msg, re.IGNORECASE):
            score += points
            tags.append(tag)

    # Penalize boring commits
    for pattern, penalty in BORING_MSG_PATTERNS:
        if re.search(pattern, msg, re.IGNORECASE):
            score += penalty  # penalty is negative

    # Score based on diff content
    diff_lower = diff_snippet.lower() if diff_snippet else ""
    for pattern, points, tag in INTERESTING_DIFF_PATTERNS:
        if re.search(pattern, diff_lower):
            score += points
            tags.append(tag)

    # Score based on files touched
    for filepath in files:
        for pattern, points, tag in INTERESTING_FILE_PATTERNS:
            if re.search(pattern, filepath, re.IGNORECASE):
                score += points
                if tag not in tags:
                    tags.append(tag)
                break

    # Big changes are often interesting (but not always)
    total_changed = additions + deletions
    if total_changed > 500:
        score += 3
        tags.append("large_change")
    if total_changed > 2000:
        score += 3
        tags.append("massive_change")

    # Single-file surgical fixes can be very interesting
    if len(files) == 1 and 1 <= total_changed <= 10:
        score += 4
        tags.append("surgical_fix")

    # Files deleted entirely can be dramatic
    if deletions > 200 and additions < 20:
        score += 4
        tags.append("major_deletion")

    # First commit in the repo gets a boost
    # (handled by caller checking index == 0)

    return score, list(set(tags))


def process_batch(repo_path, commits_batch, max_diff_lines, batch_num, total_batches):
    """Process a batch of commits and return scored results."""
    results = []
    batch_size = len(commits_batch)
    for i, commit in enumerate(commits_batch):
        if (i + 1) % 25 == 0 or i == 0:
            print(
                f"  Batch {batch_num}/{total_batches}: commit {i + 1}/{batch_size}...",
                file=sys.stderr,
            )

        files, additions, deletions = get_commit_diff_stat(repo_path, commit["hash"])

        # Only fetch full diff snippet for commits that pass an initial filter
        # (saves time on obviously boring commits)
        quick_score, _ = score_commit(commit, files, additions, deletions, "")
        if quick_score >= 0:
            diff_snippet = get_commit_diff_snippet(
                repo_path, commit["hash"], max_diff_lines
            )
        else:
            diff_snippet = ""

        score, tags = score_commit(commit, files, additions, deletions, diff_snippet)

        results.append(
            {
                "hash": commit["hash"][:12],
                "full_hash": commit["hash"],
                "date": commit["date"],
                "message": commit["message"],
                "files_changed": len(files),
                "files": files[:20],  # cap file list
                "additions": additions,
                "deletions": deletions,
                "score": score,
                "tags": tags,
                "diff_snippet": diff_snippet if score >= 5 else "",
            }
        )
    return results


def build_timeline_summary(scored_commits):
    """Group commits by month and identify narrative arcs."""
    months = {}
    for c in scored_commits:
        try:
            date = datetime.fromisoformat(c["date"].replace("Z", "+00:00"))
            key = date.strftime("%Y-%m")
        except (ValueError, AttributeError):
            key = "unknown"
        if key not in months:
            months[key] = {"count": 0, "interesting": 0, "top_tags": {}}
        months[key]["count"] += 1
        if c["score"] >= 5:
            months[key]["interesting"] += 1
        for tag in c["tags"]:
            months[key]["top_tags"][tag] = months[key]["top_tags"].get(tag, 0) + 1
    # Keep only top 5 tags per month
    for key in months:
        tags = months[key]["top_tags"]
        months[key]["top_tags"] = dict(
            sorted(tags.items(), key=lambda x: x[1], reverse=True)[:5]
        )
    return months


def main():
    parser = argparse.ArgumentParser(
        description="Scan git commits and score them for dev log interestingness."
    )
    parser.add_argument("--repo", default=".", help="Path to git repo")
    parser.add_argument(
        "--output", default="commits_scan.json", help="Output JSON path"
    )
    parser.add_argument("--batch-size", type=int, default=50, help="Commits per batch")
    parser.add_argument(
        "--max-diff-lines", type=int, default=200, help="Max diff lines per commit"
    )
    parser.add_argument("--since", default=None, help="Only after date (YYYY-MM-DD)")
    parser.add_argument("--until", default=None, help="Only before date (YYYY-MM-DD)")
    parser.add_argument("--branch", default=None, help="Branch to scan")
    parser.add_argument(
        "--min-score", type=int, default=5, help="Minimum score to include in output"
    )
    args = parser.parse_args()

    repo_path = Path(args.repo).resolve()
    if not (repo_path / ".git").exists():
        print(f"[ERROR] Not a git repository: {repo_path}", file=sys.stderr)
        sys.exit(1)

    # Get repo name
    repo_name = run_git(["rev-parse", "--show-toplevel"], repo_path)
    repo_name = Path(repo_name.strip()).name if repo_name else repo_path.name

    # Get current branch
    branch = args.branch
    if not branch:
        branch = run_git(["rev-parse", "--abbrev-ref", "HEAD"], repo_path)
        branch = branch.strip() if branch else "unknown"

    print(f"Scanning repo: {repo_name} (branch: {branch})", file=sys.stderr)

    # Get all commits
    commits = get_commit_list(repo_path, args.branch, args.since, args.until)
    total = len(commits)
    print(f"Found {total} commits", file=sys.stderr)

    if total == 0:
        print("[ERROR] No commits found.", file=sys.stderr)
        sys.exit(1)

    # Boost first commit
    if commits:
        commits[0]["_is_first"] = True

    # Process in batches
    batch_size = args.batch_size
    all_results = []
    total_batches = (total + batch_size - 1) // batch_size

    for batch_num in range(total_batches):
        start = batch_num * batch_size
        end = min(start + batch_size, total)
        batch = commits[start:end]
        results = process_batch(
            repo_path, batch, args.max_diff_lines, batch_num + 1, total_batches
        )
        # Boost first commit score
        if batch_num == 0 and results:
            results[0]["score"] += 10
            if "origin" not in results[0]["tags"]:
                results[0]["tags"].append("origin")
        all_results.extend(results)

    # Filter to interesting commits
    interesting = [c for c in all_results if c["score"] >= args.min_score]
    interesting.sort(key=lambda c: c["score"], reverse=True)

    # Build timeline
    timeline = build_timeline_summary(all_results)

    # Assemble output
    output = {
        "repo": repo_name,
        "branch": branch,
        "total_commits": total,
        "scanned": len(all_results),
        "interesting_count": len(interesting),
        "min_score_used": args.min_score,
        "timeline_summary": timeline,
        "interesting_commits": interesting,
    }

    output_path = Path(args.output)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(output, f, indent=2, ensure_ascii=False)

    print(
        f"\nDone! {len(interesting)}/{total} commits scored as interesting.",
        file=sys.stderr,
    )
    print(f"Output written to: {output_path}", file=sys.stderr)

    # Print top 10 preview
    print(f"\nTop 10 most interesting commits:", file=sys.stderr)
    for c in interesting[:10]:
        print(
            f"  [{c['score']:3d}] {c['hash']} {c['date'][:10]} "
            f"{c['message'][:60]} [{', '.join(c['tags'])}]",
            file=sys.stderr,
        )


if __name__ == "__main__":
    main()
