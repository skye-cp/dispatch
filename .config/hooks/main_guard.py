"""Pre-commit hook: main takes merges only.

Blocks a commit on main unless it is a merge (MERGE_HEAD exists) or nothing is
staged, which keeps `pre-commit run --all-files` green on a clean main.
"""

import os
import subprocess
import sys


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=False)


def main():
    if git("branch", "--show-current").stdout.strip() != "main":
        return 0
    git_dir = git("rev-parse", "--git-dir").stdout.strip()
    if os.path.exists(os.path.join(git_dir, "MERGE_HEAD")):
        return 0
    if git("diff", "--cached", "--quiet").returncode == 0:
        return 0
    print("main takes merges only: commit on a branch, then git merge --no-ff")
    return 1


if __name__ == "__main__":
    sys.exit(main())
