"""Isolate exactly which field in the .NET request causes the 400."""
import json, os, urllib.request, urllib.error
from pathlib import Path

# Reuse token logic from main test
import sys
sys.path.insert(0, str(Path(__file__).parent))
from copilot_api_test import get_github_token, get_copilot_token, COPILOT_HEADERS

github_token = get_github_token()
copilot_token = get_copilot_token(github_token)

with open(Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-102405-143.json") as f:
    original = json.load(f)

print("Original top-level keys:", list(original.keys()))
print("tool_choice:", original.get("tool_choice"))
print("model:", original.get("model"))
print("tools count:", len(original.get("tools", [])))
print("messages count:", len(original.get("messages", [])))


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
            data = json.loads(resp.read())
            content = data["choices"][0]["message"]["content"][:80]
            print(f"  PASS {label}: {content}")
            return True
    except urllib.error.HTTPError as e:
        err = e.read().decode()[:200]
        print(f"  FAIL {label}: HTTP {e.code} — {err}")
        return False


# Test 0: Original as-is (should 400)
print("\n=== TEST 0: Original payload as-is ===")
send(original, "original")

# Test 1: Add content=null to assistant message
print("\n=== TEST 1: Add content:null to assistant message ===")
t1 = json.loads(json.dumps(original))
for m in t1["messages"]:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
send(t1, "content:null")

# Test 2: Remove tool_choice
print("\n=== TEST 2: Remove tool_choice ===")
t2 = json.loads(json.dumps(original))
t2.pop("tool_choice", None)
send(t2, "no tool_choice")

# Test 3: Both fixes (content:null + no tool_choice)
print("\n=== TEST 3: content:null + no tool_choice ===")
t3 = json.loads(json.dumps(original))
for m in t3["messages"]:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
t3.pop("tool_choice", None)
send(t3, "both fixes")

# Test 4: Fix \r\n in arguments
print("\n=== TEST 4: Fix \\r\\n in tool_call arguments ===")
t4 = json.loads(json.dumps(original))
for m in t4["messages"]:
    if m["role"] == "assistant" and m.get("tool_calls"):
        for tc in m["tool_calls"]:
            tc["function"]["arguments"] = tc["function"]["arguments"].replace("\r\n", "\n")
send(t4, "fix crlf")

# Test 5: All three fixes
print("\n=== TEST 5: All fixes (content:null + no tool_choice + fix crlf) ===")
t5 = json.loads(json.dumps(original))
for m in t5["messages"]:
    if m["role"] == "assistant":
        if "content" not in m:
            m["content"] = None
        if m.get("tool_calls"):
            for tc in m["tool_calls"]:
                tc["function"]["arguments"] = tc["function"]["arguments"].replace("\r\n", "\n")
t5.pop("tool_choice", None)
send(t5, "all fixes")

# Test 6: Minimal reproduction - just system+user+assistant(tool_calls)+tool, 2 tools
print("\n=== TEST 6: Minimal repro (4 messages, 2 tools) ===")
t6 = {
    "model": original["model"],
    "temperature": original["temperature"],
    "messages": list(original["messages"]),  # keep all 4 messages
    "tools": original["tools"][:2],  # only 2 tools
}
# Fix the assistant message
for m in t6["messages"]:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
send(t6, "minimal 2 tools")

# Test 7: Minimal with all 104 tools
print("\n=== TEST 7: Minimal repro with all 104 tools ===")
t7 = {
    "model": original["model"],
    "temperature": original["temperature"],
    "messages": list(original["messages"]),
    "tools": original["tools"],
}
for m in t7["messages"]:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
send(t7, "minimal 104 tools")

# Test 8: Original but with only the load_skill tool defined
print("\n=== TEST 8: Original with only load_skill tool ===")
t8 = json.loads(json.dumps(original))
t8["tools"] = [t for t in t8["tools"] if t["function"]["name"] == "load_skill"]
for m in t8["messages"]:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
t8.pop("tool_choice", None)
send(t8, "only load_skill")
