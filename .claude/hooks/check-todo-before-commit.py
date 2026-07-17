import sys
import json
import os

REPO = "C:/Users/DalyF/Documents/GitHub/unity-bridge"
TODO_PATH = "C:/Users/DalyF/Documents/GitHub/Unity MCP/TODO.md"
STATE_FILE = os.path.join(REPO, ".claude", "hooks", ".todo-mtime-state.json")

# TODO.md lives in the sandbox project (Unity MCP), which is not a git repo,
# while commits happen in this package repo. git status can't track a file
# outside any working tree, so this adapts Squelch's git-status check to an
# mtime comparison instead. See README.md DECISIONS (2026-07-17).


def main():
    try:
        data = json.load(sys.stdin)
    except Exception:
        return

    command = (data.get("tool_input", {}) or {}).get("command", "") or ""
    if "git commit" not in command:
        return

    try:
        current_mtime = os.path.getmtime(TODO_PATH)
    except OSError:
        print(json.dumps({
            "systemMessage": f"TODO.md not found at {TODO_PATH} — can't verify "
                              "it was updated before this commit."
        }))
        return

    baseline = 0.0
    try:
        with open(STATE_FILE, "r", encoding="utf-8") as f:
            baseline = json.load(f).get("baselineMtime", 0.0)
    except (OSError, json.JSONDecodeError):
        pass

    if current_mtime > baseline:
        os.makedirs(os.path.dirname(STATE_FILE), exist_ok=True)
        with open(STATE_FILE, "w", encoding="utf-8") as f:
            json.dump({"baselineMtime": current_mtime}, f)
        return

    print(json.dumps({
        "systemMessage": "TODO.md hasn't changed since the last commit — "
                          "update the live tracker if this commit needs it "
                          "(see Working Rules in TODO.md)."
    }))


if __name__ == "__main__":
    main()
