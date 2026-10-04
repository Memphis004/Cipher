#!/usr/bin/env python3
"""Stage 7 helper: call one tool on the local Unity MCP bridge over JSON-RPC.

The bridge speaks streamable HTTP on the session path recorded in .agents/mcp.json.
A fresh session is created per call, so the editor can be restarted between calls
without this script holding a stale session id.

Usage:
    tools/mcp_call.py <tool_name> '<json-args>'
    tools/mcp_call.py <tool_name> --input-file args.json

--input-file exists because most of the interesting calls here carry a whole C# file as
a string argument, and passing that through a shell is a quoting accident waiting to
happen.
"""
import json
import os
import sys
import urllib.request

DEFAULT_ENDPOINT = os.environ.get(
    "PROJECTSPY_MCP_ENDPOINT", "http://localhost:25116/p/6c5c99b2"
)


def _post(endpoint, session_id, payload, timeout):
    """POST one JSON-RPC message and return the decoded `data:` payload."""
    body = json.dumps(payload).encode("utf-8")
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
    }
    if session_id:
        headers["Mcp-Session-Id"] = session_id

    req = urllib.request.Request(endpoint, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read().decode("utf-8", "replace")
        new_session = resp.headers.get("Mcp-Session-Id") or session_id

    # Streamable HTTP replies as SSE; take the first JSON message frame.
    for line in raw.splitlines():
        if line.startswith("data: "):
            return json.loads(line[6:]), new_session
    raise RuntimeError("no data frame in MCP response: " + raw[:400])


def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 2

    tool = argv[1]
    rest = argv[2:]

    if rest and rest[0] == "--input-file":
        if len(rest) < 2:
            print("--input-file needs a path", file=sys.stderr)
            return 2
        with open(rest[1], encoding="utf-8") as handle:
            args = json.load(handle)
    elif rest:
        args = json.loads(rest[0])
    else:
        args = {}

    endpoint = DEFAULT_ENDPOINT
    timeout = float(os.environ.get("PROJECTSPY_MCP_TIMEOUT", "300"))

    init, session = _post(
        endpoint,
        None,
        {
            "jsonrpc": "2.0",
            "id": 1,
            "method": "initialize",
            "params": {
                "protocolVersion": "2024-11-05",
                "capabilities": {},
                "clientInfo": {"name": "projectspy-stage7", "version": "1"},
            },
        },
        30,
    )
    if "error" in init:
        print(json.dumps(init), file=sys.stderr)
        return 3

    # The server refuses tools/call until it has seen the initialized notification.
    try:
        _post(endpoint, session, {"jsonrpc": "2.0", "method": "notifications/initialized"}, 15)
    except Exception:
        pass

    result, _ = _post(
        endpoint,
        session,
        {
            "jsonrpc": "2.0",
            "id": 2,
            "method": "tools/call",
            "params": {"name": tool, "arguments": args},
        },
        timeout,
    )

    # Unwrap the MCP content envelope so callers get the tool's own text.
    payload = result.get("result", result)
    if isinstance(payload, dict) and "content" in payload:
        texts = [c.get("text", "") for c in payload["content"] if c.get("type") == "text"]
        print("\n".join(texts))
        return 0 if not payload.get("isError") else 1

    print(json.dumps(payload, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))