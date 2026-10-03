#!/usr/bin/env python3
"""Spike #119: compaction and a fresh context on a long-lived `codex app-server` session.

Uses the thread parameters of CodexSessionDriver (ephemeral thread, on-request + read-only, experimentalApi). Proves,
in one process: thread/compact/start on an empty thread, on a thread with a marker and an instruction (contextCompaction
item lifecycle, token usage before/after), recall after compaction, an interrupted compaction, compaction requested
during an active turn, and a fresh thread/start with the same settings as the "clear" (marker gone, model and policies
kept) followed by thread/unsubscribe of the old thread. Ids are printed as the server returns them; no account data is read.

Usage: python3 codex_clear_compact_spike.py <scratch-dir>
"""
import json
import queue
import subprocess
import sys
import threading
import time

cwd = sys.argv[1]
MARKER = "ZEBRA-4712"
proc = subprocess.Popen(["codex", "app-server", "--listen", "stdio://"], cwd=cwd, stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, bufsize=1)
inbox: "queue.Queue[dict]" = queue.Queue()


def reader():
    for line in proc.stdout:
        if line.strip():
            inbox.put(json.loads(line))
    inbox.put({"__eof__": True})


threading.Thread(target=reader, daemon=True).start()
next_id = 0
SKIP = {"item/agentMessage/delta", "item/reasoning/summaryTextDelta", "item/reasoning/summaryPartAdded",
        "mcpServer/startupStatus/updated", "remoteControl/status/changed", "account/rateLimits/updated",
        "turn/diff/updated", "item/reasoning/textDelta"}


def send(obj):
    print(">>", json.dumps(obj)[:300], flush=True)
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def request(method, params):
    global next_id
    next_id += 1
    send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params})
    return next_id


def show(msg):
    method = msg.get("method")
    params = msg.get("params", {})
    if method == "thread/tokenUsage/updated":
        usage = params["tokenUsage"]
        return f"tokenUsage thread={params['threadId']} last.input={usage['last']['inputTokens']} " \
               f"total.input={usage['total']['inputTokens']} window={usage.get('modelContextWindow')}"
    if method in ("item/started", "item/completed"):
        item = params["item"]
        text = item.get("text")
        return f"{method} type={item['type']} thread={params.get('threadId')} turn={params.get('turnId')}" + \
               (f" text={text[:200]!r}" if text else "")
    if method == "turn/completed":
        turn = params["turn"]
        return f"turn/completed thread={params['threadId']} status={turn['status']} error={turn.get('error')}"
    if "result" in msg and isinstance(msg["result"], dict) and "thread" in msg["result"]:
        r = msg["result"]
        return "thread/start result " + json.dumps({"id": r["thread"]["id"], "model": r.get("model"),
                                                    "reasoningEffort": r.get("reasoningEffort"),
                                                    "approvalPolicy": r.get("approvalPolicy"),
                                                    "sandbox": r.get("sandbox"), "cwd": r.get("cwd")})
    return json.dumps(msg)[:400]


def pump(until, timeout=300):
    while True:
        msg = inbox.get(timeout=timeout)
        if msg.get("__eof__"):
            print("<< EOF")
            return msg
        if msg.get("method") in SKIP:
            continue
        if "method" in msg and "id" in msg:
            send({"jsonrpc": "2.0", "id": msg["id"], "result": {"decision": "decline"}})
        print("<<", show(msg), flush=True)
        if until(msg):
            return msg


def reply_to(rid):
    return lambda m: m.get("id") == rid and "method" not in m


def start_thread():
    rid = request("thread/start", {"cwd": cwd, "approvalPolicy": "on-request", "approvalsReviewer": "user",
                                   "sandbox": "read-only", "ephemeral": True, "model": model})
    return pump(reply_to(rid))["result"]["thread"]["id"]


def turn(thread, text, effort="low"):
    started = time.monotonic()
    rid = request("turn/start", {"threadId": thread, "effort": effort, "input": [{"type": "text", "text": text}]})
    turn_id = pump(reply_to(rid))["result"]["turn"]["id"]
    pump(lambda m: m.get("method") == "turn/completed" and m["params"]["turn"]["id"] == turn_id)
    print(f"== {text[:30]!r}: {(time.monotonic() - started):.1f} s", flush=True)


def compact_started(thread):
    """thread/compact/start answers {} at once; the compaction runs as a turn of its own on the same thread."""
    rid = request("thread/compact/start", {"threadId": thread})
    reply = pump(reply_to(rid))
    if "error" in reply:
        return None
    return pump(lambda m: m.get("method") == "turn/started" and m["params"]["threadId"] == thread)["params"]["turn"]["id"]


def compact(thread):
    started = time.monotonic()
    turn_id = compact_started(thread)
    if turn_id:
        pump(lambda m: m.get("method") == "turn/completed" and m["params"]["turn"]["id"] == turn_id)
    print(f"== compact: {(time.monotonic() - started):.1f} s", flush=True)


def settle(thread, seconds=20):
    """Prints everything for a while, to see what a racing request leaves behind."""
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            msg = inbox.get(timeout=max(0.1, deadline - time.monotonic()))
        except queue.Empty:
            return
        if msg.get("method") not in SKIP:
            print("<<", show(msg), flush=True)


rid = request("initialize", {"clientInfo": {"name": "dante-spike", "version": "0.0.0"},
                             "capabilities": {"experimentalApi": True}})
pump(reply_to(rid))
send({"jsonrpc": "2.0", "method": "initialized"})
model = None
rid = request("thread/start", {"cwd": cwd, "approvalPolicy": "on-request", "approvalsReviewer": "user",
                               "sandbox": "read-only", "ephemeral": True})
first = pump(reply_to(rid))["result"]
model = first["model"]
old = first["thread"]["id"]

print("### empty thread")
compact(old)
print("### conversation")
turn(old, f"Memorize the code word {MARKER}. Instruction for the rest of this conversation: always answer in "
          "UPPERCASE. Reply only: OK")
turn(old, "Which code word did I ask you to memorize? Answer with the code word only.")
print("### compact")
compact(old)
turn(old, "Which code word did I ask you to memorize, and which instruction applies to your answers? Be brief.")
print("### interrupted compact")
turn_id = compact_started(old)
request("turn/interrupt", {"threadId": old, "turnId": turn_id})
pump(lambda m: m.get("method") == "turn/completed" and m["params"]["turn"]["id"] == turn_id)
turn(old, "Which code word did I ask you to memorize? Answer with the code word only.")
print("### compact during an active turn")
rid = request("turn/start", {"threadId": old, "effort": "low", "input": [{"type": "text", "text":
        "Count slowly from 1 to 40, one number per line."}]})
pump(reply_to(rid))
request("thread/compact/start", {"threadId": old})
settle(old, 40)
turn(old, "Which code word did I ask you to memorize? Answer with the code word only.")
print("### clear = fresh thread with the same settings")
new = start_thread()
turn(new, "Which code word did I ask you to memorize earlier? If you do not know, answer exactly: UNKNOWN")
compact(new)
rid = request("thread/unsubscribe", {"threadId": old})
pump(reply_to(rid))

proc.stdin.close()
print("exit code:", proc.wait(timeout=60))
