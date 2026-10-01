#!/usr/bin/env python3
"""Spike #93: what `codex app-server` accepts as media in the session protocol the driver uses.

Turn 1: text + two `localImage` inputs in one turn/start.
Turn 2: text + one `localAudio` input (declared in the schema; checked here, not assumed).
Turn 3: a long text turn steered with text + `localImage` through turn/steer.
Turn 4: a request to generate an image, to see whether an `imageGeneration` item arrives and where it is saved.
Approvals are declined. Only completed items, errors and turn results are printed; image data is never printed.

Usage: python3 codex_media_spike.py <scratch-dir> <media-dir>   (media from make_fixtures.py + speech.wav)
"""
import json
import queue
import subprocess
import sys
import threading

cwd, media = sys.argv[1], sys.argv[2]
proc = subprocess.Popen(["codex", "app-server", "--listen", "stdio://"], cwd=cwd, stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, bufsize=1)
inbox: "queue.Queue[dict]" = queue.Queue()
threading.Thread(target=lambda: [inbox.put(json.loads(l)) for l in proc.stdout if l.strip()], daemon=True).start()
next_id = 0


def request(method, params):
    global next_id
    next_id += 1
    proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params}) + "\n")
    proc.stdin.flush()
    return next_id


def pump(until, timeout=600):
    while True:
        msg = inbox.get(timeout=timeout)
        method, params = msg.get("method"), msg.get("params", {})
        if "method" in msg and "id" in msg:  # server request: decline approvals
            print("<< server request", method, "-> decline")
            proc.stdin.write(json.dumps({"jsonrpc": "2.0", "id": msg["id"], "result": {"decision": "decline"}}) + "\n")
            proc.stdin.flush()
        elif method == "item/completed":
            item = params["item"]
            kind = item["type"]
            if kind == "agentMessage":
                print("<< agentMessage:", item["text"])
            elif kind == "imageGeneration":
                print("<< imageGeneration:", json.dumps({k: item.get(k) for k in
                      ("status", "savedPath", "failure", "revisedPrompt")}, ensure_ascii=False),
                      "| result chars:", len(item.get("result") or ""))
            elif kind == "userMessage":
                print("<< userMessage content types:", [c.get("type") for c in item.get("content", [])])
            else:
                print("<< item:", kind, json.dumps({k: item.get(k) for k in ("path", "command", "status")
                                                     if k in item}, ensure_ascii=False)[:200])
        elif method in ("error", "warning") or "error" in msg:
            print("<<", method or "error response", json.dumps(params or msg.get("error"), ensure_ascii=False)[:400])
        elif method == "turn/completed":
            print("<< turn/completed:", params["turn"].get("status"), json.dumps(params["turn"].get("error"))[:300])
        if until(msg):
            return msg


rid = request("initialize", {"clientInfo": {"name": "dante-spike", "version": "0.0.0"}})
pump(lambda m: m.get("id") == rid)
proc.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "initialized"}) + "\n")
proc.stdin.flush()
rid = request("thread/start", {"cwd": cwd, "approvalPolicy": "untrusted", "sandbox": "workspace-write",
                               "ephemeral": True})
thread = pump(lambda m: m.get("id") == rid)["result"]["thread"]["id"]


def turn(label, items):
    print(f"\n=== {label}")
    request("turn/start", {"threadId": thread, "input": items})
    pump(lambda m: m.get("method") == "turn/completed")


turn("turno 1: localImage x2", [
    {"type": "text", "text": "Duas imagens anexadas. Para cada uma, diga em uma linha as cores e formas. "
                             "Não execute comandos."},
    {"type": "localImage", "path": f"{media}/left-red-right-blue.png"},
    {"type": "localImage", "path": f"{media}/green-square.png"}])
turn("turno 2: localAudio", [
    {"type": "text", "text": "Transcreva a fala do áudio anexado, se conseguir ouvir. Se não, diga por quê. "
                             "Não execute comandos."},
    {"type": "localAudio", "path": f"{media}/speech.wav"}])

print("\n=== turno 3: turn/steer com localImage")
rid = request("turn/start", {"threadId": thread, "input": [{"type": "text", "text":
              "Escreva um poema de 40 linhas sobre rios. Não execute comandos."}]})
active = pump(lambda m: m.get("id") == rid)["result"]["turn"]["id"]
request("turn/steer", {"threadId": thread, "expectedTurnId": active, "input": [
    {"type": "text", "text": "Mudança de plano: pare o poema e diga só as cores da imagem anexada."},
    {"type": "localImage", "path": f"{media}/left-red-right-blue.png"}]})
pump(lambda m: m.get("method") == "turn/completed")

turn("turno 4: geração de imagem", [{"type": "text", "text":
     "Gere uma imagem simples: um círculo amarelo sobre fundo preto. Não execute comandos de shell."}])

proc.stdin.close()
print("\nexit:", proc.wait(timeout=60))
