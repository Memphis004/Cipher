#!/usr/bin/env python3
"""Capture a Unity screenshot and write the PNG to disk.

The MCP screenshot tools return the image inside the JSON-RPC envelope as base64. The
text-only client drops it, so this saves the bytes and prints the path, which is what
makes a visual check of the blockout actually possible.

Usage:
    tools/unity_shot.py scene-view out.png [width height]
    tools/unity_shot.py camera out.png [width height]
"""
import base64
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
MCP = ROOT / "tools" / "mcp_call_raw.py"

TOOL = {
    "scene-view": "screenshot-scene-view",
    "camera": "screenshot-camera",
    "game-view": "screenshot-game-view",
}


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2

    which = argv[1]
    out = Path(argv[2])
    width = int(argv[3]) if len(argv) > 3 else 1400
    height = int(argv[4]) if len(argv) > 4 else 800

    tool = TOOL.get(which)
    if tool is None:
        print(f"unknown shot type {which}")
        return 2

    proc = subprocess.run(
        [sys.executable, str(MCP), tool, json.dumps({"width": width, "height": height})],
        capture_output=True, text=True, timeout=300,
    )

    for line in proc.stdout.splitlines():
        line = line.strip()
        if line.startswith("data:"):
            payload = json.loads(line[5:])
            for part in payload.get("result", {}).get("content", []):
                if part.get("type") == "image" and part.get("data"):
                    out.parent.mkdir(parents=True, exist_ok=True)
                    out.write_bytes(base64.b64decode(part["data"]))
                    print(f"wrote {out} ({out.stat().st_size} bytes)")
                    return 0
            print("no image in response:", json.dumps(payload)[:400])
            return 1

    print("no data frame:", proc.stdout[:300], proc.stderr[:300])
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))