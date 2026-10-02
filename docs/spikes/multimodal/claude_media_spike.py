#!/usr/bin/env python3
"""Spike #93: what Claude Code accepts as media in the session protocol the driver uses (stream-json).

Turn 1: two PNGs as base64 `image` content blocks next to the text, in a single user message.
Turn 2: no blocks; the prompt names a PNG inside the working directory, so the agent must open it with Read.
Turn 3: the same with speech.wav (make_speech.ps1), to see whether audio reaches the model at all. Without
speech.wav only this turn is skipped, and says so: tone.wav has no speech, so it cannot test transcription.
can_use_tool requests are allowed only for Read and denied otherwise. Only the assistant text, tool calls and
results are printed; the base64 payloads are not.

Usage: python3 claude_media_spike.py <scratch-dir> <media-dir>   (make_fixtures.py; speech.wav optional)
"""
import base64
import json
import queue
import shutil
import subprocess
import sys
import threading
from pathlib import Path

cwd, media = Path(sys.argv[1]), Path(sys.argv[2])
(cwd / "attachments").mkdir(exist_ok=True)
shutil.copy(media / "green-square.png", cwd / "attachments" / "green-square.png")
speech = media / "speech.wav"
if speech.exists():
    shutil.copy(speech, cwd / "attachments" / "speech.wav")

proc = subprocess.Popen(
    ["claude", "--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
     "--permission-mode", "manual", "--permission-prompt-tool", "stdio", "--no-session-persistence"],
    cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, bufsize=1)
inbox: "queue.Queue[dict]" = queue.Queue()
threading.Thread(target=lambda: [inbox.put(json.loads(l)) for l in proc.stdout if l.strip()], daemon=True).start()


def send(obj):
    proc.stdin.write(json.dumps(obj) + "\n")
    proc.stdin.flush()


def image_block(path):
    return {"type": "image", "source": {"type": "base64", "media_type": "image/png",
                                        "data": base64.b64encode(path.read_bytes()).decode()}}


def turn(content, label):
    print(f"\n=== {label}")
    send({"type": "user", "message": {"role": "user", "content": content}})
    while True:
        msg = inbox.get(timeout=300)
        kind = msg.get("type")
        if kind == "control_request" and msg["request"].get("subtype") == "can_use_tool":
            request = msg["request"]
            allowed = request["tool_name"] == "Read"
            print(f"<< can_use_tool {request['tool_name']} {json.dumps(request['input'])} -> {'allow' if allowed else 'deny'}")
            send({"type": "control_response", "response": {"subtype": "success", "request_id": msg["request_id"],
                  "response": {"behavior": "allow", "updatedInput": request["input"]} if allowed else
                  {"behavior": "deny", "message": "negado pelo spike"}}})
        elif kind == "assistant":
            for block in msg["message"]["content"]:
                if block["type"] == "text":
                    print("<< text:", block["text"])
                elif block["type"] == "tool_use":
                    print("<< tool_use:", block["name"], json.dumps(block["input"]))
        elif kind == "user":
            for block in msg["message"].get("content", []):
                if isinstance(block, dict) and block.get("type") == "tool_result":
                    content = block.get("content")
                    kinds = [c.get("type") for c in content] if isinstance(content, list) else ["text"]
                    preview = content if isinstance(content, str) else ""
                    print("<< tool_result:", kinds, "error" if block.get("is_error") else "", preview[:200])
        elif kind == "result":
            print("<< result:", msg.get("subtype"), "| turns:", msg.get("num_turns"))
            return


turn([{"type": "text", "text": "Duas imagens anexadas. Para cada uma, diga as cores e formas que você vê, "
                                "em uma linha por imagem. Não use ferramentas."},
      image_block(media / "left-red-right-blue.png"), image_block(media / "green-square.png")],
     "turno 1: blocos image base64")
turn("Abra attachments/green-square.png com a ferramenta Read e descreva as cores e a forma central.",
     "turno 2: imagem no workspace, aberta pelo agente")
if speech.exists():
    turn("Abra attachments/speech.wav com a ferramenta Read e transcreva a fala, se conseguir ouvir. "
         "Se não conseguir, diga apenas por quê.", "turno 3: áudio no workspace")
else:
    print("\n=== turno 3: áudio no workspace — PULADO: speech.wav ausente (gere com make_speech.ps1)")
proc.stdin.close()
print("\nexit:", proc.wait(timeout=60))
