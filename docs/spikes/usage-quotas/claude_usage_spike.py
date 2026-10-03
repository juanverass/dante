#!/usr/bin/env python3
"""Spike #115: read the Claude subscription quotas over stream-json.

Proves: the `get_usage` control request (experimental in 2.1.287) answered with
no user message (no turn), its latency, and the same request issued while a turn
is running (optional `--with-turn`). `rate_limit_event` messages are printed when
they appear. Session cost, account identifiers and local behaviors are left out
of the printout.

Usage: python3 claude_usage_spike.py <scratch-dir> [--with-turn]
"""
import json
import queue
import subprocess
import sys
import threading
import time

cwd = sys.argv[1]
with_turn = "--with-turn" in sys.argv
proc = subprocess.Popen(
    ["claude", "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
     "--permission-mode", "manual", "--permission-prompt-tool", "stdio", "--no-session-persistence"],
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


def control(request):
    global next_id
    next_id += 1
    rid = f"dante-{next_id}"
    send({"type": "control_request", "request_id": rid, "request": request})
    return rid


def summarize(msg):
    if msg.get("type") == "control_response":
        response = dict(msg["response"])
        body = response.get("response")
        if isinstance(body, dict) and "rate_limits_available" in body:
            # Keep only the quota part; session cost and local behaviors are out of scope.
            response["response"] = {k: body.get(k) for k in
                                    ("subscription_type", "rate_limits_available", "rate_limits")}
        elif isinstance(body, dict):
            response["response"] = {"keys": sorted(body)}
        return {"type": "control_response", "response": response}
    if msg.get("type") in ("rate_limit_event", "result"):
        return {k: v for k, v in msg.items() if k not in ("session_id", "uuid", "result", "usage", "modelUsage")}
    return {k: msg[k] for k in ("type", "subtype") if k in msg}


def pump(until, timeout=120):
    while True:
        msg = inbox.get(timeout=timeout)
        if msg.get("__eof__"):
            print("<< EOF")
            return msg
        if msg.get("type") == "control_request":
            print("<< control_request", msg["request"].get("subtype"), flush=True)
            send({"type": "control_response", "response": {"subtype": "success", "request_id": msg["request_id"],
                  "response": {"behavior": "deny", "message": "spike"}}})
        elif msg.get("type") != "stream_event":
            print("<<", json.dumps(summarize(msg))[:3000], flush=True)
        if until(msg):
            return msg


def is_response(rid):
    return lambda m: m.get("type") == "control_response" and m["response"].get("request_id") == rid


def timed(request):
    started = time.monotonic()
    pump(is_response(control(request)))
    print(f"== {request['subtype']}: {(time.monotonic() - started) * 1000:.0f} ms", flush=True)


pump(is_response(control({"subtype": "initialize"})))
timed({"subtype": "get_usage", "skip_behaviors": True})
timed({"subtype": "get_usage", "skip_behaviors": True})

if with_turn:
    send({"type": "user", "message": {"role": "user", "content": "Count from 1 to 30, one number per line."}})
    pump(lambda m: m.get("type") == "system" and m.get("subtype") == "init")
    timed({"subtype": "get_usage", "skip_behaviors": True})
    pump(lambda m: m.get("type") == "result", timeout=180)
    timed({"subtype": "get_usage", "skip_behaviors": True})

proc.stdin.close()
print("exit code:", proc.wait(timeout=30))
