"""Isolate: which field in the messages causes 400?"""
import json, os, urllib.request, urllib.error
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).parent))
from copilot_api_test import get_github_token, get_copilot_token, COPILOT_HEADERS

copilot_token = get_copilot_token(get_github_token())

with open(Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-111351-559.json") as f:
    d = json.load(f)

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
            data = json.loads(resp.read())
            print("  PASS %s" % label)
            return True
    except urllib.error.HTTPError as e:
        err = e.read().decode()[:100]
        print("  FAIL %s: HTTP %d %s" % (label, e.code, err))
        return False

base_tools = d["tools"][:2]  # just 2 tools, no dupes

# Working baseline from our Python tests — we know this works
asst_msg = d["messages"][2]  # assistant with tool_calls
tool_msgs = [m for m in d["messages"] if m["role"] == "tool"]

# Build a known-good version
tc0 = asst_msg["tool_calls"][0]
tc1 = asst_msg["tool_calls"][1]

good_asst = {
    "role": "assistant",
    "content": None,
    "tool_calls": [
        {"id": tc0["id"], "type": "function",
         "function": {"name": tc0["function"]["name"],
                       "arguments": tc0["function"]["arguments"].replace("\r\n", "\n")}},
        {"id": tc1["id"], "type": "function",
         "function": {"name": tc1["function"]["name"],
                       "arguments": "{}"}},  # "null" -> "{}"
    ],
}
good_tools = [
    {"role": "tool", "tool_call_id": tc0["id"], "content": tool_msgs[0]["content"][:500].replace("\r\n", "\n")},
    {"role": "tool", "tool_call_id": tc1["id"], "content": tool_msgs[1]["content"][:500].replace("\r\n", "\n")},
]

base = {
    "model": "claude-sonnet-4.6",
    "temperature": 0.3,
    "messages": [
        {"role": "user", "content": "Show me the scripts."},
        good_asst,
        *good_tools,
    ],
    "tools": base_tools,
}

# Test 0: Known-good
print("=== Clean messages ===")
send(base, "clean")

# Test 1: Add "name" field to assistant
print("\n=== Add name:CEAIOperator to assistant ===")
t1 = json.loads(json.dumps(base))
t1["messages"][1]["name"] = "CEAIOperator"
send(t1, "with-name")

# Test 2: arguments="null" instead of "{}"
print('\n=== arguments="null" ===')
t2 = json.loads(json.dumps(base))
t2["messages"][1]["tool_calls"][1]["function"]["arguments"] = "null"
send(t2, "args-null")

# Test 3: Missing content key on assistant (not null, just missing)
print("\n=== content key missing on assistant ===")
t3 = json.loads(json.dumps(base))
del t3["messages"][1]["content"]
send(t3, "no-content-key")

# Test 4: content="" on assistant
print('\n=== content="" on assistant ===')
t4 = json.loads(json.dumps(base))
t4["messages"][1]["content"] = ""
send(t4, "empty-content")

# Test 5: \\r\\n in tool content
print("\n=== \\r\\n in tool content ===")
t5 = json.loads(json.dumps(base))
t5["messages"][2]["content"] = tool_msgs[0]["content"][:500]  # with \r\n
send(t5, "crlf-content")

# Test 6: name + args null + no content key (all .NET quirks combined)
print("\n=== All .NET quirks: name + null args + no content key ===")
t6 = json.loads(json.dumps(base))
t6["messages"][1]["name"] = "CEAIOperator"
t6["messages"][1]["tool_calls"][1]["function"]["arguments"] = "null"
del t6["messages"][1]["content"]
send(t6, "all-quirks")

# Test 7: Just name + no content key
print("\n=== name + no content key ===")
t7 = json.loads(json.dumps(base))
t7["messages"][1]["name"] = "CEAIOperator"
del t7["messages"][1]["content"]
send(t7, "name+no-content")

# Test 8: Just args null + no content key
print('\n=== null args + no content key ===')
t8 = json.loads(json.dumps(base))
t8["messages"][1]["tool_calls"][1]["function"]["arguments"] = "null"
del t8["messages"][1]["content"]
send(t8, "null-args+no-content")
