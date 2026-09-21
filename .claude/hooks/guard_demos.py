"""PreToolUse guard for the demo corpus.

On 2026-09-20 an agent made demos/ reachable from a git worktree with a Windows directory
junction, then ran `git worktree remove --force`. The removal followed the junction and deleted
the TARGET's contents: 2.8 GB of .dem files. demos/ is gitignored, so git could not restore any
of it and no copy existed on any drive.

This ASKS rather than denies. The point is that a human sees the command before it runs, not that
the operation becomes impossible. Silence (exit 0, no output) means the command is none of our
business, which is the common case.
"""
import json
import re
import sys

DESTRUCTIVE = re.compile(
    r"(?:^|[\s|&;(])(rm|rmdir|del|erase|Remove-Item)(?:\s|$)", re.IGNORECASE)
DEMOS = re.compile(r"(?:^|[^A-Za-z0-9_-])demos(?:[^A-Za-z0-9_-]|$)")
WORKTREE = re.compile(r"git\s+worktree\s+(remove|prune)", re.IGNORECASE)
LINKING = re.compile(
    r"mklink|New-Item\s+.*-ItemType\s*(SymbolicLink|Junction)", re.IGNORECASE)


def ask(reason):
    json.dump({"hookSpecificOutput": {
        "hookEventName": "PreToolUse",
        "permissionDecision": "ask",
        "permissionDecisionReason": reason,
    }}, sys.stdout)
    sys.stdout.write("\n")
    sys.exit(0)


def main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        return  # Never block a command because the guard could not parse its own input.

    command = (payload.get("tool_input") or {}).get("command") or ""
    if not command:
        return

    # The exact mechanism. Worth confirming every time: the damage is silent and total.
    if WORKTREE.search(command):
        ask("git worktree remove/prune follows Windows directory junctions and deletes the "
            "TARGET's contents rather than the link. This destroyed the 2.8 GB demo corpus on "
            "2026-09-20. Confirm nothing inside the worktree points out of it, or unlink with "
            "rmdir first.")

    # Anything destructive aimed at the corpus.
    if DESTRUCTIVE.search(command) and DEMOS.search(command):
        ask("This deletes under demos/. That directory is gitignored, so nothing in it is "
            "recoverable from git, and the corpus has already been lost once. Explicit approval "
            "required.")

    # The precursor. Without the junction, the removal would have taken only the worktree.
    if LINKING.search(command) and DEMOS.search(command):
        ask("Linking demos/ into another tree is what allowed a worktree removal to delete it. "
            "DemoTestHelper honours the DEMO_PATH environment variable; use that instead.")


main()
