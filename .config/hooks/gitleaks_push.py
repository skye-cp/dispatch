"""Pre-push hook: scan the commits being pushed for secrets with gitleaks.

Needs the gitleaks binary on PATH (for example `winget install Gitleaks.Gitleaks`).
"""

import os
import shutil
import subprocess
import sys


def main():
    gitleaks = shutil.which("gitleaks")
    if not gitleaks:
        print("gitleaks is not on PATH. Install it (winget install Gitleaks.Gitleaks) and push again.")
        return 1
    to_ref = os.environ.get("PRE_COMMIT_TO_REF") or "HEAD"
    cmd = [
        gitleaks, "git", "--no-banner", "--redact",
        "-c", ".config/gitleaks.toml",
        f"--log-opts={to_ref} --not --remotes",
    ]
    return subprocess.run(cmd, check=False).returncode


if __name__ == "__main__":
    sys.exit(main())
