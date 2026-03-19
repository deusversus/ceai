"""Confirm: is it duplicate tool names or tool count that causes 400?"""
import json, os, urllib.request, urllib.error
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).parent))
from copilot_api_test import get_github_token, get_copilot_token, COPILOT_HEADERS

github_token = get_github_token()
copilot_token = get_copilot_token(github_token)

with open(Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-102405-143.json") as f:
    original = json.load(f)

def send(payload, label):
    body = json.dumps(payload).encode()
    req = urllib.request.Request(
        "https://api.githubcopilot.com/chat/completions",
        data=body,
        headers={
            "Authorization": "Bearer " + copilot_token,
            "Content-Type": "application/json",
            **COPILOT_HEADERS,
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(req) as resp:
            json.loads(resp.read())
            print("  PASS " + label + " [" + str(len(payload.get("tools",[]))) + " tools]")
            return True
    except urllib.error.HTTPError as e:
        e.read()
        print("  FAIL " + label + " [" + str(len(payload.get("tools",[]))) + " tools]: HTTP " + str(e.code))
        return False

base_msgs = [
    {"role": "system", "content": "You are a helpful assistant."},
    {"role": "user", "content": "Say hi in 3 words."},
]

# Show duplicate tool names
names = [t["function"]["name"] for t in original["tools"]]
from collections import Counter
dupes = {k:v for k,v in Counter(names).items() if v > 1}
print("Duplicate tool names:", dupes)
print("Unique tools:", len(set(names)), "/ Total:", len(names))

# Test A: Deduplicated tools (keep first occurrence)
print("\n=== TEST A: Deduplicated tools (unique names only) ===")
seen = set()
deduped = []
for t in original["tools"]:
    name = t["function"]["name"]
    if name not in seen:
        seen.add(name)
        deduped.append(t)
print("Deduped count:", len(deduped))
p = {"model": "claude-sonnet-4.6", "temperature": 0.3, "messages": base_msgs, "tools": deduped}
send(p, "deduped")

# Test B: 104 tools but all unique names (pad with dummy tools)
print("\n=== TEST B: 104 unique tools ===")
unique_104 = list(deduped)
i = 0
while len(unique_104) < 104:
    unique_104.append({
        "type": "function",
        "function": {
            "name": "dummy_pad_" + str(i),
            "description": "Padding tool " + str(i),
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    })
    i += 1
p = {"model": "claude-sonnet-4.6", "temperature": 0.3, "messages": base_msgs, "tools": unique_104}
send(p, "104-unique")

# Test C: Original 104 with duplicates
print("\n=== TEST C: Original 104 tools (with duplicates) ===")
p = {"model": "claude-sonnet-4.6", "temperature": 0.3, "messages": base_msgs, "tools": original["tools"]}
send(p, "104-with-dupes")

# Test D: Just 3 tools but with duplicates
print("\n=== TEST D: 3 tools with 1 duplicate name ===")
dup_tools = [
    {"type": "function", "function": {"name": "hello", "description": "Say hi", "parameters": {"type": "object", "properties": {}, "required": []}}},
    {"type": "function", "function": {"name": "world", "description": "Say world", "parameters": {"type": "object", "properties": {}, "required": []}}},
    {"type": "function", "function": {"name": "hello", "description": "Say hi again", "parameters": {"type": "object", "properties": {}, "required": []}}},
]
p = {"model": "claude-sonnet-4.6", "temperature": 0.3, "messages": base_msgs, "tools": dup_tools}
send(p, "3-with-dupe")

# Test E: Full tool-result conversation with deduped tools
print("\n=== TEST E: Full tool-result conversation, deduped tools ===")
clean_msgs = list(original["messages"])
for m in clean_msgs:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
p = {"model": "claude-sonnet-4.6", "temperature": 0.3, "messages": clean_msgs, "tools": deduped}
send(p, "tool-result-deduped")
