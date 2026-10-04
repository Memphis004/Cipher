#!/usr/bin/env python3
"""Refresh Unity, wait for compilation, and report only what matters.

The MCP console log is dominated by stack traces from unrelated plugin warnings, so this
filters to compile diagnostics and prints each distinct message once. Used after every
batch of Stage 7 edits, because a compile error that is only visible by scrolling a
thousand lines of console noise is a compile error that gets missed.

Usage:
    tools/check_compile.py           # refresh, wait, report
    tools/check_compile.py --quiet   # report pass/fail only
"""
import json
import re
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MCP = ROOT / "tools" / "mcp_call.py"

# `path(line): error CSxxxx: message` as emitted by the Unity compiler.
CS_ERROR = re.compile(r"^(?P<file>[^(]+)\((?P<line>\d+),\d+\): (?:error|warning) CS\d+:")
OTHER_ERR = re.compile(r"^\s*(?P<file>[^\s]+\.(?:cs|asmdef|json))\s*[:(]")

BANNED = ("Assembly for Assembly Definition File", "immutable packages", "NuGet restore")

# Touching this file guarantees a compilation is triggered, which is what stops the log
# read above from reporting an empty console as a pass. Its contents are irrelevant; its
# mere existence change is the signal.
PROBE_PATH = "Assets/ProjectSpy/CompileProbe.cs"
PROBE_SOURCE = """// Compile probe. Rewritten by tools/check_compile.py purely to force Unity to
// recompile, so that a cleared console cannot be mistaken for a clean build.
namespace ProjectSpy.Unity
{
    internal static class CompileProbe
    {
        public static int Value => 1;
    }
}
"""


def call(tool, args, timeout="900"):
    proc = subprocess.run(
        [sys.executable, str(MCP), tool, json.dumps(args)],
        capture_output=True, text=True, timeout=int(timeout),
    )
    return proc.stdout


def main(argv):
    quiet = "--quiet" in argv

    call("console-clear-logs", {})
    refresh = call("assets-refresh", {})
    if "compilation errors" in refresh.lower():
        # Refresh reports early; the editor is still writing diagnostics.
        time.sleep(3)

    # Poll until the editor stops compiling, so a slow compile is not read as a pass.
    for _ in range(60):
        state = call("editor-application-get-state", {})
        if '"IsCompiling":false' in state:
            break
        time.sleep(2)

    # A refresh that finds nothing to do does not recompile, which means the console was
    # cleared and then read as empty — a false pass. Force a real recompile so the log we
    # are about to read reflects the files currently on disk.
    call("script-update-or-create", {
        "path": PROBE_PATH,
        "csharpCode": PROBE_SOURCE,
        "waitForCompletion": True,
    })
    for _ in range(60):
        state = call("editor-application-get-state", {})
        if '"IsCompiling":false' in state:
            break
        time.sleep(2)

    logs = call("console-get-logs", {"limit": 400})
    saw_any_log = "[]" not in logs.strip()[-4:]

    errors, warnings = [], []
    try:
        entries = json.loads(logs).get("result", [])
    except json.JSONDecodeError:
        print("could not parse console log")
        return 2

    seen = set()
    for entry in entries:
        message = entry.get("Message", "")
        if any(b in message for b in BANNED):
            continue
        for line in message.splitlines():
            line = line.strip()
            if not line:
                continue
            m = CS_ERROR.match(line)
            if m:
                key = (m.group("file"), m.group("line"), line)
                if key in seen:
                    continue
                seen.add(key)
                (errors if "error CS" in line else warnings).append(line)
                continue
            if line.startswith(("Assets", "Packages", "ProjectSettings")) and (
                "error" in line.lower() or "exception" in line.lower()
            ):
                if line in seen:
                    continue
                seen.add(line)
                errors.append(line)

    if not quiet:
        print(f"compile errors: {len(errors)}")
        for line in errors[:60]:
            print("  " + line)
        if warnings:
            print(f"compile warnings: {len(warnings)}")
            for line in warnings[:25]:
                print("  ~ " + line)
    else:
        print("PASS" if not errors else f"FAIL ({len(errors)} errors)")

    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))