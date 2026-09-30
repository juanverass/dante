#!/usr/bin/env python3
"""Spike #61: drive Claude Code with bidirectional stream-json (JSONL).

Proves: one long-lived `claude --print` process with --input-format stream-json,
two turns on stdin, a permission prompt answered by the host (control protocol),
an interrupt control request, and clean shutdown by closing stdin. Run from an
empty scratch directory; it prints every message.

Usage: python3 claude_stream_json_spike.py <scratch-dir>
"""
import json
import subprocess
import sys
import threading
import queue

cwd = sys.argv[1]
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
    print(">>", json.dumps(obj)[:300], flush=True)
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def user(text):
    send({"type": "user", "message": {"role": "user", "content": text}})


def control(request):
    global next_id
    next_id += 1
    send({"type": "control_request", "request_id": f"dante-{next_id}", "request": request})


def pump(until, timeout=180, on_permission=None):
    """Print messages until `until(msg)` is true; answer can_use_tool control requests."""
    while True:
        msg = inbox.get(timeout=timeout)
        if msg.get("__eof__"):
            print("<< EOF")
            return msg
        print("<<", msg.get("type"), msg.get("subtype", ""), json.dumps(msg)[:260], flush=True)
        if msg.get("type") == "control_request" and msg["request"].get("subtype") == "can_use_tool":
            send({"type": "control_response", "response": {
                "subtype": "success", "request_id": msg["request_id"], "response": on_permission(msg)}})
        if until(msg):
            return msg


is_result = lambda m: m.get("type") == "result"

control({"subtype": "initialize"})
pump(lambda m: m.get("type") == "control_response")

# Turn 1: plain answer, no tools.
user("Reply with exactly: pong")
pump(is_result)

# Turn 2 (same process, same session): a write must go through the host permission prompt; deny it.
user("Create a file named spike.txt containing 'hi' using the Write tool. If denied, stop and say so.")
pump(is_result, on_permission=lambda m: {"behavior": "deny", "message": "Negado pelo spike do D.A.N.T.E."})


def answer_question(m):
    # AskUserQuestion arrives as can_use_tool; the answer goes back inside updatedInput.
    question = m["request"]["input"]["questions"][0]["question"]
    return {"behavior": "allow", "updatedInput": {**m["request"]["input"], "answers": {question: "blue"}}}


# Turn 2b: user input requested by the agent.
user("Use the AskUserQuestion tool to ask me which color I prefer, red or blue. Then reply with only the color.")
pump(is_result, on_permission=answer_question)

# Turn 3: start a long answer and interrupt it.
user("Count slowly from 1 to 500, one number per line.")
pump(lambda m: m.get("type") in ("assistant", "stream_event", "system") and m.get("subtype") != "init"
     or is_result(m))
control({"subtype": "interrupt"})
pump(is_result)

# Turn 4: a message sent while the previous turn is running is queued for the next boundary.
user("Write a 40-line poem about rivers.")
user("Reply with exactly: second")
pump(is_result)
pump(is_result, timeout=90)

proc.stdin.close()
print("exit code:", proc.wait(timeout=30))
