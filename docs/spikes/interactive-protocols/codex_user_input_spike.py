#!/usr/bin/env python3
"""Spike #61: human input over `codex app-server --listen stdio://` (JSON-RPC in JSONL).

Proves the full round trip of the EXPERIMENTAL server request `item/tool/requestUserInput`:
the request reaches the host, the host answers it, and the answer reaches the model.
On 0.157.1 the request_user_input tool only exists in the `plan` collaboration mode, which
needs `capabilities.experimentalApi` at initialize. Run from an empty scratch directory.

Usage: python3 codex_user_input_spike.py <scratch-dir> <model>
"""
import json
import subprocess
import sys
import threading
import queue

cwd, model = sys.argv[1], sys.argv[2]
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
ANSWER = "blue-heron-42"


def send(obj):
    print(">>", json.dumps(obj)[:400], flush=True)
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def request(method, params):
    global next_id
    next_id += 1
    send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params})
    return next_id


def answer_user_input(msg):
    # Every question gets the same free-text answer; the model must echo it back.
    return {"answers": {q["id"]: {"answers": [ANSWER]} for q in msg["params"]["questions"]}}


def pump(until, timeout=300):
    while True:
        msg = inbox.get(timeout=timeout)
        if msg.get("__eof__"):
            print("<< EOF")
            return msg
        summary = {k: msg[k] for k in ("id", "method") if k in msg}
        print("<<", json.dumps(summary), json.dumps(msg.get("params", msg.get("result", msg.get("error"))))[:400],
              flush=True)
        if "method" in msg and "id" in msg:
            if msg["method"] == "item/tool/requestUserInput":
                send({"jsonrpc": "2.0", "id": msg["id"], "result": answer_user_input(msg)})
            else:
                send({"jsonrpc": "2.0", "id": msg["id"], "result": {"decision": "decline"}})
        if until(msg):
            return msg


rid = request("initialize", {"clientInfo": {"name": "dante-spike", "version": "0.0.0"},
                             "capabilities": {"experimentalApi": True}})
pump(lambda m: m.get("id") == rid)
send({"jsonrpc": "2.0", "method": "initialized"})

rid = request("thread/start", {"cwd": cwd, "approvalPolicy": "untrusted", "sandbox": "read-only",
                               "ephemeral": True})
thread = pump(lambda m: m.get("id") == rid)["result"]["thread"]["id"]

request("turn/start", {
    "threadId": thread,
    "collaborationMode": {"mode": "plan", "settings": {"model": model, "developer_instructions": None}},
    "input": [{"type": "text", "text":
               "Before anything else, use the request_user_input tool to ask me one question: "
               "'What is the project codename?'. Then reply with exactly the codename I gave you "
               "and nothing else. Do not run commands or read files."}]})
pump(lambda m: m.get("method") == "turn/completed")

proc.stdin.close()
print("exit code:", proc.wait(timeout=30))
