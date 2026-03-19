import json, os
from pathlib import Path
from collections import Counter

with open(Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-111028-737.json") as f:
    d = json.load(f)

print("Model:", d.get("model"))
print("Top-level keys:", list(d.keys()))
print("Messages:", len(d["messages"]))
print("Tools:", len(d.get("tools", [])))
print("Stream:", d.get("stream"))

names = [t["function"]["name"] for t in d.get("tools", [])]
dupes = {k: v for k, v in Counter(names).items() if v > 1}
print("Duplicate tools:", dupes if dupes else "NONE")
print("Unique names:", len(set(names)))

for m in d["messages"]:
    role = m["role"]
    has_content = "content" in m
    content_val = m.get("content")
    content_type = type(content_val).__name__ if has_content else "MISSING"
    extra = set(m.keys()) - {"role", "content", "tool_calls", "tool_call_id", "name"}
    tc_count = len(m.get("tool_calls", []))
    print("  %s: content_key=%s content_type=%s tool_calls=%d extra=%s" % (role, has_content, content_type, tc_count, extra))
    if role == "tool":
        print("    tool_call_id:", m.get("tool_call_id"))
        c = m.get("content", "")
        print("    content[:80]:", repr(c[:80]))
    if role == "assistant" and tc_count > 0:
        tc = m["tool_calls"][0]
        print("    tc.id:", tc["id"])
        print("    tc.function:", tc["function"]["name"])

# Now replay it with deduped tools via Python SDK
print("\n=== REPLAY TEST ===")
import urllib.request, urllib.error
import sys
sys.path.insert(0, str(Path(__file__).parent))
from copilot_api_test import get_github_token, get_copilot_token, COPILOT_HEADERS

copilot_token = get_copilot_token(get_github_token())

# Test as-is
body = json.dumps(d).encode()
req = urllib.request.Request(
    "https://api.githubcopilot.com/chat/completions",
    data=body,
    headers={"Authorization": "Bearer " + copilot_token, "Content-Type": "application/json", **COPILOT_HEADERS},
    method="POST",
)
try:
    with urllib.request.urlopen(req) as resp:
        print("As-is: PASS", resp.status)
except urllib.error.HTTPError as e:
    print("As-is: FAIL HTTP", e.code, e.read().decode()[:100])

# Test with stream=false
d2 = dict(d)
d2["stream"] = False
body2 = json.dumps(d2).encode()
req2 = urllib.request.Request(
    "https://api.githubcopilot.com/chat/completions",
    data=body2,
    headers={"Authorization": "Bearer " + copilot_token, "Content-Type": "application/json", **COPILOT_HEADERS},
    method="POST",
)
try:
    with urllib.request.urlopen(req2) as resp:
        print("stream=false: PASS", resp.status)
except urllib.error.HTTPError as e:
    print("stream=false: FAIL HTTP", e.code, e.read().decode()[:100])

# Test with content:null on assistant + no stream key
d3 = json.loads(json.dumps(d))
d3.pop("stream", None)
for m in d3["messages"]:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
body3 = json.dumps(d3).encode()
req3 = urllib.request.Request(
    "https://api.githubcopilot.com/chat/completions",
    data=body3,
    headers={"Authorization": "Bearer " + copilot_token, "Content-Type": "application/json", **COPILOT_HEADERS},
    method="POST",
)
try:
    with urllib.request.urlopen(req3) as resp:
        print("content:null+no-stream: PASS", resp.status)
except urllib.error.HTTPError as e:
    print("content:null+no-stream: FAIL HTTP", e.code, e.read().decode()[:100])
