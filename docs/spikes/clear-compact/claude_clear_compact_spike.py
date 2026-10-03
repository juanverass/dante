#!/usr/bin/env python3
"""Spike #119: /compact and /clear on a long-lived Claude Code stream-json session.

Uses the same arguments as ClaudeSessionDriver for a General Mode session (manual profile). Proves, in one process:
a marker and an instruction survive /compact (compact_boundary with pre_tokens), the marker is gone after /clear,
the session settings (model, permission mode, cwd, tools) are kept, and how both commands behave on an empty
conversation and when interrupted. Context size is read with get_context_usage before and after each operation.
Session ids are printed; account data and cost are left out.

Usage: python3 claude_clear_compact_spike.py <scratch-dir>
"""
import json
import queue
import subprocess
import sys
import threading
import time
import uuid

cwd = sys.argv[1]
MARKER = "ZEBRA-4712"
proc = subprocess.Popen(
    ["claude", "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
     "--session-id", str(uuid.uuid4()), "--permission-mode", "default", "--permission-prompt-tool", "stdio",
     "--restricted", "--strict-mcp-config", "--tools", "Read,Write,Edit,AskUserQuestion"],
    cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, bufsize=1)
inbox: "queue.Queue[dict]" = queue.Queue()


def reader():
    for line in proc.stdout:
        if line.strip():
            inbox.put(json.loads(line))
    inbox.put({"__eof__": True})


threading.Thread(target=reader, daemon=True).start()
next_id = 0


def send(obj):
    print(">>", json.dumps(obj)[:200], flush=True)
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def control(request):
    global next_id
    next_id += 1
    rid = f"dante-{next_id}"
    send({"type": "control_request", "request_id": rid, "request": request})
    return rid


def show(msg):
    kind, sub = msg.get("type"), msg.get("subtype")
    if kind == "system" and sub == "init":
        keep = {k: msg.get(k) for k in ("session_id", "model", "permissionMode", "cwd")}
        keep["tools"] = len(msg.get("tools") or [])
        return f"system/init {json.dumps(keep)}"
    if kind == "system":
        return f"system/{sub} " + json.dumps({k: v for k, v in msg.items() if k not in ("type", "subtype", "uuid")})[:600]
    if kind == "assistant":
        text = " ".join(b.get("text", "") for b in msg["message"]["content"] if b.get("type") == "text")
        return f"assistant session={msg.get('session_id')} text={text[:300]!r}"
    if kind == "result":
        return "result " + json.dumps({k: msg.get(k) for k in
                                       ("subtype", "is_error", "num_turns", "session_id", "result", "terminal_reason")})[:500]
    if kind == "control_response":
        body = msg["response"].get("response") or {}
        if "totalTokens" in body:
            return "context " + json.dumps({k: body.get(k) for k in ("totalTokens", "maxTokens", "percentage")})
        return "control_response " + json.dumps({k: msg["response"].get(k) for k in ("subtype", "request_id", "error")})
    return f"{kind}/{sub} " + json.dumps(msg)[:300]


def pump(until, timeout=300):
    while True:
        msg = inbox.get(timeout=timeout)
        if msg.get("__eof__"):
            print("<< EOF")
            return msg
        if msg.get("type") in ("stream_event", "rate_limit_event"):
            continue
        if msg.get("type") == "control_request":
            print("<< control_request", msg["request"].get("subtype"))
            send({"type": "control_response", "response": {"subtype": "success", "request_id": msg["request_id"],
                  "response": {"behavior": "deny", "message": "spike"}}})
            continue
        print("<<", show(msg), flush=True)
        if until(msg):
            return msg


def turn(text):
    started = time.monotonic()
    send({"type": "user", "message": {"role": "user", "content": text}})
    result = pump(lambda m: m.get("type") == "result")
    print(f"== {text[:30]!r}: {(time.monotonic() - started):.1f} s", flush=True)
    return result


def context():
    rid = control({"subtype": "get_context_usage"})
    pump(lambda m: m.get("type") == "control_response" and m["response"].get("request_id") == rid)


print("### empty conversation")
turn("/compact")
print("### conversation")
turn(f"Memorize the code word {MARKER}. Instruction for the rest of this conversation: always answer in UPPERCASE. "
     "Reply only: OK")
turn("Which code word did I ask you to memorize? Answer with the code word only.")
context()
print("### compact")
turn("/compact")
context()
turn("Which code word did I ask you to memorize, and which instruction applies to your answers? Be brief.")
print("### clear")
turn("/clear")
context()
turn("Which code word did I ask you to memorize earlier? If you do not know, answer exactly: UNKNOWN")
print("### compact right after clear")
turn("/compact")
print("### interrupted compact")
turn(f"Memorize the code word {MARKER}-B. Reply only: OK")
send({"type": "user", "message": {"role": "user", "content": "/compact"}})
time.sleep(1.5)
control({"subtype": "interrupt"})
pump(lambda m: m.get("type") == "result")
turn("Which code word did I ask you to memorize? Answer with the code word only.")

proc.stdin.close()
print("exit code:", proc.wait(timeout=60))
