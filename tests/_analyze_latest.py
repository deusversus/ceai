import json, os
from pathlib import Path
from collections import Counter

with open(Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-111351-559.json") as f:
    d = json.load(f)

print("Tools:", len(d.get("tools", [])))
names = [t["function"]["name"] for t in d.get("tools", [])]
dupes = {k: v for k, v in Counter(names).items() if v > 1}
print("Duplicates:", dupes if dupes else "NONE")
print("Top-level keys:", list(d.keys()))
print("stream:", d.get("stream"))
print("stream_options:", d.get("stream_options"))

for m in d["messages"]:
    role = m["role"]
    has_content = "content" in m
    ctype = type(m["content"]).__name__ if has_content else "MISSING"
    print("  %s: content=%s(%s) tool_calls=%d" % (role, has_content, ctype, len(m.get("tool_calls", []))))

# Replay with fix: no dupes, no stream, content:null
print("\n=== Replay: deduped + no stream + content:null ===")
import urllib.request, urllib.error
import sys
sys.path.insert(0, str(Path(__file__).parent))
from copilot_api_test import get_github_token, get_copilot_token, COPILOT_HEADERS
copilot_token = get_copilot_token(get_github_token())

fixed = json.loads(json.dumps(d))
# Deduplicate tools
seen = set()
deduped = []
for t in fixed["tools"]:
    n = t["function"]["name"]
    if n not in seen:
        seen.add(n)
        deduped.append(t)
fixed["tools"] = deduped
# Fix missing content on assistant
for m in fixed["messages"]:
    if m["role"] == "assistant" and "content" not in m:
        m["content"] = None
# Remove stream
fixed.pop("stream", None)
fixed.pop("stream_options", None)

print("  Deduped tools:", len(fixed["tools"]))
body = json.dumps(fixed).encode()
req = urllib.request.Request(
    "https://api.githubcopilot.com/chat/completions",
    data=body,
    headers={"Authorization": "Bearer " + copilot_token, "Content-Type": "application/json", **COPILOT_HEADERS},
    method="POST",
)
try:
    with urllib.request.urlopen(req) as resp:
        data = json.loads(resp.read())
        msg = data["choices"][0]["message"]
        print("  PASS:", (msg.get("content") or "(tool_calls)")[:80])
except urllib.error.HTTPError as e:
    print("  FAIL: HTTP", e.code, e.read().decode()[:200])

# Also try: deduped but WITH stream=true
print("\n=== Replay: deduped + stream=true ===")
fixed2 = json.loads(json.dumps(fixed))
fixed2["stream"] = True
body2 = json.dumps(fixed2).encode()
req2 = urllib.request.Request(
    "https://api.githubcopilot.com/chat/completions",
    data=body2,
    headers={"Authorization": "Bearer " + copilot_token, "Content-Type": "application/json", **COPILOT_HEADERS},
    method="POST",
)
try:
    with urllib.request.urlopen(req2) as resp:
        raw = resp.read().decode()
        print("  PASS (streamed, first 200):", raw[:200])
except urllib.error.HTTPError as e:
    print("  FAIL: HTTP", e.code, e.read().decode()[:200])
