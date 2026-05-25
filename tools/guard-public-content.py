#!/usr/bin/env python3
import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path


def run_git(repo_root, *args):
    result = subprocess.run(
        ["git", *args],
        cwd=repo_root,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return result.stdout.decode(errors="replace")


def get_repo_root():
    result = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"],
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return Path(result.stdout.decode(errors="replace").strip()).resolve()


def is_inside_repo(repo_root, full_path):
    root = os.path.normcase(str(repo_root))
    candidate = os.path.normcase(str(full_path))
    try:
        return os.path.commonpath([root, candidate]) == root
    except ValueError:
        return False


def load_config(script_dir):
    config_path = script_dir / "public-content-blocklist.json"
    with config_path.open("r", encoding="utf-8") as handle:
        config = json.load(handle)

    blocked_patterns = []
    for entry in config["blockedPatterns"]:
        pattern = "".join(entry["patternParts"])
        blocked_patterns.append(
            {
                "label": entry["label"],
                "regex": re.compile(pattern),
            }
        )

    return {
        "blockedPatterns": blocked_patterns,
        "includeExtensions": set(config["includeExtensions"]),
        "ignoredPathParts": tuple(config["ignoredPathParts"]),
    }


def test_text_file(path, include_extensions):
    name = path.name
    suffix = path.suffix
    return name in include_extensions or suffix in include_extensions


def should_skip(relative_path, ignored_path_parts):
    normalized = "/" + relative_path.replace("\\", "/")
    return any(part.lower() in normalized.lower() for part in ignored_path_parts)


def candidate_paths(repo_root, staged):
    if staged:
        output = run_git(repo_root, "diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z")
    else:
        output = run_git(repo_root, "ls-files", "-z")
    return [path for path in output.split("\0") if path]


def read_text(path):
    return path.read_bytes().decode("utf-8-sig", errors="replace")


def main():
    parser = argparse.ArgumentParser(description="Scan public repository content for private references.")
    parser.add_argument("--staged", action="store_true", help="Scan only staged files.")
    args = parser.parse_args()

    script_dir = Path(__file__).resolve().parent
    repo_root = get_repo_root()
    config = load_config(script_dir)

    findings = []
    for relative_path in candidate_paths(repo_root, args.staged):
        full_path = (repo_root / relative_path).resolve()
        if full_path != repo_root and not is_inside_repo(repo_root, full_path):
            findings.append(f"{relative_path}: path resolves outside repository")
            continue
        if not full_path.is_file():
            continue
        if should_skip(relative_path, config["ignoredPathParts"]):
            continue
        if not test_text_file(full_path, config["includeExtensions"]):
            continue

        content = read_text(full_path)
        for blocked in config["blockedPatterns"]:
            match = blocked["regex"].search(content)
            if match:
                line_number = content.count("\n", 0, match.start()) + 1
                findings.append(f"{relative_path}:{line_number}: {blocked['label']} -> '{match.group(0)}'")

    if findings:
        print("Public content guard failed. Remove or rewrite these references before committing:", file=sys.stderr)
        for finding in findings:
            print(f"  - {finding}", file=sys.stderr)
        return 1

    print("Public content guard passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
