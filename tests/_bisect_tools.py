"""Binary search: how many tools before Copilot 400s?"""
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
            data = json.loads(resp.read())
            msg = data["choices"][0]["message"]
            result = msg.get("content") or "(tool_calls returned)"
            print("  PASS " + label + " [" + str(len(payload.get("tools",[]))) + " tools, " + str(len(body)) + "B]: " + str(result)[:60])
            return True
    except urllib.error.HTTPError as e:
        err = e.read().decode()[:200]
        print("  FAIL " + label + " [" + str(len(payload.get("tools",[]))) + " tools, " + str(len(body)) + "B]: HTTP " + str(e.code) + " - " + err)
        return False

def make_payload(tool_count):
    p = {
        "model": original["model"],
        "temperature": original["temperature"],
        "messages": json.loads(json.dumps(original["messages"])),
        "tools": original["tools"][:tool_count],
    }
    for m in p["messages"]:
        if m["role"] == "assistant" and "content" not in m:
            m["content"] = None
    return p

# First: test without tool result messages (just system+user) but WITH many tools
print("=== Phase 1: Many tools, NO tool result (simple chat) ===")
simple_msgs = [m for m in original["messages"] if m["role"] in ("system", "user")]
for count in [2, 10, 50, 80, 100, 104]:
    p = {
        "model": original["model"],
        "temperature": original["temperature"],
        "messages": simple_msgs,
        "tools": original["tools"][:count],
    }
    send(p, "simple+" + str(count))

# Phase 2: With tool result, binary search tool count
print("\n=== Phase 2: WITH tool result, varying tool count ===")
for count in [2, 5, 10, 20, 50, 80, 100, 104]:
    send(make_payload(count), str(count) + "-tools")

# Phase 3: If all fail with tool result, test with minimal tool set
# but check each tool definition for problems
print("\n=== Phase 3: Validate individual tool schemas ===")
# Check if any tool has problematic schema
for i, tool in enumerate(original["tools"]):
    fn = tool["function"]
    params = fn.get("parameters", {})
    props = params.get("properties", {})
    has_additional = "additionalProperties" in params
    has_required = "required" in params
    req_count = len(params.get("required", []))
    prop_count = len(props)
    # Flag anything unusual
    if has_additional or not has_required or prop_count > 10:
        print("  Tool #" + str(i) + " " + fn["name"] + ": props=" + str(prop_count) + " req=" + str(req_count) + " additionalProps=" + str(has_additional))

# Phase 4: Test with tool result but using the EXACT message format Python SDK uses
print("\n=== Phase 4: Python SDK-style messages with .NET tool schemas ===")
# Reconstruct a clean tool-result conversation
asst = [m for m in original["messages"] if m["role"] == "assistant"][0]
tool_msg = [m for m in original["messages"] if m["role"] == "tool"][0]
tc = asst["tool_calls"][0]

clean_msgs = [
    {"role": "system", "content": "You are a helpful assistant."},
    {"role": "user", "content": "Load the script-engineering skill."},
    {
        "role": "assistant",
        "content": None,
        "tool_calls": [{
            "id": tc["id"],
            "type": "function",
            "function": {
                "name": tc["function"]["name"],
                "arguments": tc["function"]["arguments"].replace("\r\n", "\n"),
            },
        }],
    },
    {
        "role": "tool",
        "tool_call_id": tc["id"],
        "content": tool_msg["content"][:500],
    },
]

# With 2 tools
p = {"model": original["model"], "temperature": 0.3, "messages": clean_msgs, "tools": original["tools"][:2]}
send(p, "clean-2tools")

# With 104 tools
p["tools"] = original["tools"]
send(p, "clean-104tools")

# With 104 tools but original system prompt
sys_msg = [m for m in original["messages"] if m["role"] == "system"][0]
clean_msgs[0] = sys_msg
p = {"model": original["model"], "temperature": 0.3, "messages": clean_msgs, "tools": original["tools"]}
send(p, "clean-104tools-realsystem")
