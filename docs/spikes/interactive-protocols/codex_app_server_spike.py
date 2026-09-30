#!/usr/bin/env python3
"""Spike #61: drive `codex app-server --listen stdio://` over JSON-RPC (JSONL).

Proves: initialize handshake, thread/start, two turns in one process, a command
approval request answered by the client, turn/interrupt, and clean shutdown by
closing stdin. Run from an empty scratch directory; it prints every message.

Usage: python3 codex_app_server_spike.py <scratch-dir>
"""
import json
import subprocess
import sys
import threading
import queue

cwd = sys.argv[1]
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
    print(">>", json.dumps(obj)[:300], flush=True)
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def request(method, params):
    global next_id
    next_id += 1
    send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params})
    return next_id


def pump(until, timeout=180, on_server_request=None):
    """Print messages until `until(msg)` is true; answer server requests."""
    while True:
        msg = inbox.get(timeout=timeout)
        if msg.get("__eof__"):
            print("<< EOF")
            return msg
        summary = {k: msg[k] for k in ("id", "method") if k in msg}
        print("<<", json.dumps(summary), json.dumps(msg.get("params", msg.get("result", msg.get("error"))))[:240],
              flush=True)
        if "method" in msg and "id" in msg and on_server_request:
            send({"jsonrpc": "2.0", "id": msg["id"], "result": on_server_request(msg)})
        if until(msg):
            return msg


rid = request("initialize", {"clientInfo": {"name": "dante-spike", "version": "0.0.0"}})
pump(lambda m: m.get("id") == rid)
send({"jsonrpc": "2.0", "method": "initialized"})

rid = request("thread/start", {"cwd": cwd, "approvalPolicy": "untrusted", "sandbox": "read-only",
                               "ephemeral": True})
thread = pump(lambda m: m.get("id") == rid)["result"]["thread"]["id"]

# Turn 1: plain answer, no tools.
request("turn/start", {"threadId": thread, "input": [{"type": "text", "text": "Reply with exactly: pong"}]})
pump(lambda m: m.get("method") == "turn/completed")

# Turn 2 (same thread, same process): a write under read-only + untrusted must ask for approval.
request("turn/start", {"threadId": thread, "input": [{"type": "text", "text":
        "Run the shell command `touch spike.txt` exactly once. If it is not allowed, stop and say so."}]})
pump(lambda m: m.get("method") == "turn/completed",
     on_server_request=lambda m: {"decision": "decline"})

# Turn 3: start a long turn and interrupt it.
rid = request("turn/start", {"threadId": thread, "input": [{"type": "text", "text":
              "Count slowly from 1 to 500, one number per line."}]})
turn = pump(lambda m: m.get("id") == rid)["result"]["turn"]["id"]
request("turn/interrupt", {"threadId": thread, "turnId": turn})
pump(lambda m: m.get("method") == "turn/completed")

# Turn 4: steer the active turn, then try a second turn/start while it is still running.
rid = request("turn/start", {"threadId": thread, "input": [{"type": "text", "text":
              "Write a 40-line poem about rivers."}]})
turn = pump(lambda m: m.get("id") == rid)["result"]["turn"]["id"]
request("turn/steer", {"threadId": thread, "expectedTurnId": turn, "input": [{"type": "text", "text":
        "Change of plan: stop the poem and reply only with the word: steered"}]})
# Observed on 0.157.1: this returns the SAME active turn id and is folded into it, like a steer.
request("turn/start", {"threadId": thread, "input": [{"type": "text", "text": "Reply with exactly: second"}]})
pump(lambda m: m.get("method") == "turn/completed")

proc.stdin.close()
print("exit code:", proc.wait(timeout=30))
