import json, os
from pathlib import Path

with open(Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-102405-143.json") as f:
    net = json.load(f)

asst = [m for m in net["messages"] if m["role"] == "assistant"][0]
print("=== ASSISTANT MESSAGE ===")
print(json.dumps(asst, indent=2)[:800])

tool = [m for m in net["messages"] if m["role"] == "tool"][0]
print("\n=== TOOL MESSAGE ===")
tp = dict(tool)
tp["content"] = tp["content"][:100] + "..."
print(json.dumps(tp, indent=2))

print("\n=== EXTRA KEYS PER MESSAGE ===")
for m in net["messages"]:
    extra = set(m.keys()) - {"role", "content", "tool_calls", "tool_call_id", "name"}
    if extra:
        print("  " + m["role"] + ": " + str(extra))

print("\n=== TOP-LEVEL KEYS ===", list(net.keys()))
if asst.get("tool_calls"):
    tc = asst["tool_calls"][0]
    print("Tool call keys:", list(tc.keys()))
    print("Function keys:", list(tc["function"].keys()))
