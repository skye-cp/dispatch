"""Pre-commit hook: block personal data from entering the repo.

Checks staged text files for:
  1. Structured patterns: email addresses, phone numbers, Brazilian CPF/CNPJ,
     card numbers, Steam IDs (SteamID64) and personal Windows/macOS/Linux home paths.
  2. A local denylist (.config/pii_wordlist.local.txt, gitignored): real names,
     machine names and other contextual terms no pattern can see.

Silence a reviewed line with the marker 'pii:allow'.
"""

import os
import re
import sys

PATTERNS = {
    "Email address": re.compile(r"\b[A-Za-z0-9._%+-]+@(?!users\.noreply\.github\.com\b)[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"),
    "Phone number": re.compile(r"(?<![\w/])\+\d{1,3}[\s-]?\(?\d{2,4}\)?[\s-]?\d{3,5}[\s-]?\d{4}\b"),
    "Brazilian CPF": re.compile(r"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b"),
    "Brazilian CNPJ": re.compile(r"\b\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}\b"),
    "Card number": re.compile(r"\b(?:4\d{3}|5[1-5]\d{2})[\s-]?\d{4}[\s-]?\d{4}[\s-]?\d{4}\b"),
    "Steam ID": re.compile(r"\b7656119\d{10}\b"),
    "Home folder path": re.compile(
        r"(?i)(?:\b[A-Z]:[\\/]+Users[\\/]+(?!Public\b|Default\b|<)[^\\/\s\"'<>]+"
        r"|/Users/(?!Shared\b|<)[^/\s\"'<>]+|/home/(?!<)[^/\s\"'<>]+)"
    ),
}

SKIP = re.compile(r"(^|/)(\.config/secrets\.baseline|\.config/hooks/check_pii\.py|\.config/pii_wordlist\.example\.txt)$")
WORDLIST = ".config/pii_wordlist.local.txt"


def load_wordlist():
    if not os.path.exists(WORDLIST):
        return []
    terms = []
    with open(WORDLIST, encoding="utf-8") as f:
        for line in f:
            term = line.strip()
            if term and not term.startswith("#"):
                terms.append((term, re.compile(r"(?<!\w)" + re.escape(term) + r"(?!\w)", re.IGNORECASE)))
    return terms


def scan(path, terms):
    found = []
    try:
        with open(path, encoding="utf-8", errors="ignore") as f:
            for number, line in enumerate(f, 1):
                if "pii:allow" in line:
                    continue
                for label, pattern in PATTERNS.items():
                    for m in pattern.finditer(line):
                        found.append((path, number, label, m.group()))
                for term, pattern in terms:
                    if pattern.search(line):
                        found.append((path, number, "Denylist term", term))
    except OSError:
        pass
    return found


def main(paths):
    terms = load_wordlist()
    findings = [hit for p in paths if not SKIP.search(p.replace("\\", "/")) for hit in scan(p, terms)]
    if not findings:
        return 0
    print("Possible personal data in staged files:\n")
    for path, number, label, value in findings:
        print(f"  {path}:{number}: {label}: {value}")
    print("\nRemove it, or mark a reviewed line with 'pii:allow'.")
    if not terms:
        print(f"Note: {WORDLIST} is missing, so only patterns were checked (template: .config/pii_wordlist.example.txt).")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
