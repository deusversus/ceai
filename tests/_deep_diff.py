"""Deep diff: what exactly is in the new failed request that Python SDK wouldn't send?"""
import json, os
from pathlib import Path

with open(Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-111351-559.json") as f:
    d = json.load(f)

# Dump full assistant message
asst = [m for m in d["messages"] if m["role"] == "assistant"][0]
print("=== ASSISTANT MESSAGE (full) ===")
print(json.dumps(asst, indent=2)[:1500])

# Dump tool messages
tools = [m for m in d["messages"] if m["role"] == "tool"]
for i, t in enumerate(tools):
    print("\n=== TOOL MESSAGE #%d ===" % i)
    preview = dict(t)
    preview["content"] = preview["content"][:200] + "..."
    print(json.dumps(preview, indent=2))

# Check tool schemas for additionalProperties
print("\n=== TOOL SCHEMA SAMPLE (first tool) ===")
print(json.dumps(d["tools"][0], indent=2))

# Check if additionalProperties causes issues
print("\n=== REPLAY: deduped + content:null + remove additionalProperties ===")
import urllib.request, urllib.error
import sys, copy
sys.path.insert(0, str(Path(__file__).parent))
from copilot_api_test import get_github_token, get_copilot_token, COPILOT_HEADERS
copilot_token = get_copilot_token(get_github_token())

def send(payload, label):
    body = json.dumps(payload).encode()
    req = urllib.request.Request(
        "https://api.githubcopilot.com/chat/completions",
        data=body,
        headers={"Authorization": "Bearer " + copilot_token, "Content-Type": "application/json", **COPILOT_HEADERS},
        method="POST",
    )
    try:
        with urllib.request.urlopen(req) as resp:
            raw = resp.read().decode()
            if payload.get("stream"):
                print("  PASS %s (streamed)" % label)
            else:
                data = json.loads(raw)
                msg = data["choices"][0]["message"]
                print("  PASS %s: %s" % (label, (msg.get("content") or "(tool_calls)")[:80]))
    except urllib.error.HTTPError as e:
        print("  FAIL %s: HTTP %d %s" % (label, e.code, e.read().decode()[:200]))

def fix_base(payload):
    """Apply all known fixes."""
    p = json.loads(json.dumps(payload))
    # Deduplicate tools
    seen = set()
    deduped = []
    for t in p["tools"]:
        if t["function"]["name"] not in seen:
            seen.add(t["function"]["name"])
            deduped.append(t)
    p["tools"] = deduped
    # Fix missing content
    for m in p["messages"]:
        if m["role"] == "assistant" and "content" not in m:
            m["content"] = None
    # Remove stream
    p.pop("stream", None)
    p.pop("stream_options", None)
    return p

# Test 1: All fixes from before
p1 = fix_base(d)
send(p1, "base-fixes")

# Test 2: Also remove additionalProperties from all tool schemas
p2 = fix_base(d)
for t in p2["tools"]:
    params = t["function"].get("parameters", {})
    params.pop("additionalProperties", None)
send(p2, "no-additionalProps")

# Test 3: Also remove tool_choice
p3 = fix_base(d)
for t in p3["tools"]:
    t["function"].get("parameters", {}).pop("additionalProperties", None)
p3.pop("tool_choice", None)
send(p3, "no-additionalProps-no-toolchoice")

# Test 4: Strip down to just 2 tools + keep the same messages
p4 = fix_base(d)
p4["tools"] = p4["tools"][:2]
send(p4, "2-tools")

# Test 5: Minimal — just user+system, no tool history, 2 tools
p5 = fix_base(d)
p5["tools"] = p5["tools"][:2]
p5["messages"] = [m for m in p5["messages"] if m["role"] in ("system", "user")]
send(p5, "2-tools-no-history")

# Test 6: Full messages but only the called tool defined
called_names = set()
for m in d["messages"]:
    if m["role"] == "assistant":
        for tc in m.get("tool_calls", []):
            called_names.add(tc["function"]["name"])
print("\nCalled tool names:", called_names)

p6 = fix_base(d)
p6["tools"] = [t for t in p6["tools"] if t["function"]["name"] in called_names]
for t in p6["tools"]:
    t["function"].get("parameters", {}).pop("additionalProperties", None)
send(p6, "only-called-tools")

# Test 7: Check if the \r\n in tool content matters
p7 = fix_base(d)
p7["tools"] = [t for t in p7["tools"] if t["function"]["name"] in called_names]
for t in p7["tools"]:
    t["function"].get("parameters", {}).pop("additionalProperties", None)
for m in p7["messages"]:
    if m["role"] == "tool":
        m["content"] = m["content"].replace("\r\n", "\n")
    if m["role"] == "assistant":
        for tc in m.get("tool_calls", []):
            tc["function"]["arguments"] = tc["function"]["arguments"].replace("\r\n", "\n")
send(p7, "only-called-tools-no-crlf")
