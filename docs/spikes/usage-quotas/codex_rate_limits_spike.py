#!/usr/bin/env python3
"""Spike #115: read the Codex subscription quotas over `codex app-server`.

Proves: `account/read` (auth type, no turn), `account/rateLimits/read` without a
thread or turn, latency, repeated reads, and the read issued while a turn is
running on the same process (optional `--with-turn`). Account identifiers and
e-mail are replaced before printing; nothing else is sent to the model unless
`--with-turn` is given.

Usage: python3 codex_rate_limits_spike.py <scratch-dir> [--with-turn]
"""
import json
import queue
import subprocess
import sys
import threading
import time

cwd = sys.argv[1]
with_turn = "--with-turn" in sys.argv
SENSITIVE = {"email", "accountId", "chatgptAccountId", "userId", "installationId", "serverName"}


def sanitize(value, nested=False):
    """Redact identifiers; `id` is redacted only below the JSON-RPC envelope (reset credits)."""
    if isinstance(value, dict):
        return {k: ("<redacted>" if v is not None and (k in SENSITIVE or (nested and k == "id"))
                    else sanitize(v, k != "id")) for k, v in value.items()}
    if isinstance(value, list):
        return [sanitize(v, True) for v in value]
    return value


proc = subprocess.Popen(
    ["codex", "app-server", "--listen", "stdio://"],
    cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
    text=True, bufsize=1)
inbox: "queue.Queue[dict]" = queue.Queue()


def reader():
    for line in proc.stdout:
        line = line.strip()
        if line:
            inbox.put(json.loads(line))
    inbox.put({"__eof__": True})


threading.Thread(target=reader, daemon=True).start()
next_id = 0


def send(obj):
    print(">>", json.dumps(obj), flush=True)
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def request(method, params):
    global next_id
    next_id += 1
    send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params})
    return next_id


def pump(until, timeout=120):
    while True:
        msg = inbox.get(timeout=timeout)
        if msg.get("__eof__"):
            print("<< EOF")
            return msg
        if msg.get("method") in ("item/agentMessage/delta",):
            continue
        print("<<", json.dumps(sanitize(msg))[:4000], flush=True)
        if "method" in msg and "id" in msg:
            send({"jsonrpc": "2.0", "id": msg["id"], "result": {"decision": "decline"}})
        if until(msg):
            return msg


def timed(method, params):
    started = time.monotonic()
    rid = request(method, params)
    msg = pump(lambda m: m.get("id") == rid)
    print(f"== {method}: {(time.monotonic() - started) * 1000:.0f} ms", flush=True)
    return msg


rid = request("initialize", {"clientInfo": {"name": "dante-spike", "version": "0.0.0"}})
pump(lambda m: m.get("id") == rid)
send({"jsonrpc": "2.0", "method": "initialized"})

timed("account/read", {})
timed("account/rateLimits/read", None)
timed("account/rateLimits/read", {"excludeResetCreditDetails": True})

if with_turn:
    rid = request("thread/start", {"cwd": cwd, "approvalPolicy": "untrusted", "sandbox": "read-only",
                                   "ephemeral": True})
    thread = pump(lambda m: m.get("id") == rid)["result"]["thread"]["id"]
    request("turn/start", {"threadId": thread, "input": [{"type": "text", "text":
            "Count slowly from 1 to 30, one number per line."}]})
    pump(lambda m: m.get("method") == "turn/started")
    # Read the quotas while the turn is running: must not interrupt or answer anything.
    timed("account/rateLimits/read", {"excludeResetCreditDetails": True})
    pump(lambda m: m.get("method") == "turn/completed", timeout=180)

proc.stdin.close()
print("exit code:", proc.wait(timeout=30))
