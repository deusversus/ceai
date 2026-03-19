"""
Standalone Copilot API test script.
Tests various request shapes to isolate what causes 400 errors.
Uses the openai Python SDK (same as the working AIDM project).

Usage:
    python tests/copilot_api_test.py

Token is auto-decrypted from CEAISuite settings via DPAPI,
or set GITHUB_TOKEN env var.
"""
import os, sys, json, time, ctypes, ctypes.wintypes, base64
from pathlib import Path

# ── DPAPI token extraction ──────────────────────────────────────────────
def dpapi_decrypt(encrypted_b64: str) -> str:
    """Decrypt a DPAPI-protected Base64 string (CurrentUser scope)."""
    encrypted = base64.b64decode(encrypted_b64)

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", ctypes.wintypes.DWORD),
                     ("pbData", ctypes.POINTER(ctypes.c_char))]

    blob_in = DATA_BLOB(len(encrypted), ctypes.create_string_buffer(encrypted, len(encrypted)))
    blob_out = DATA_BLOB()

    if not ctypes.windll.crypt32.CryptUnprotectData(
        ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out)
    ):
        raise RuntimeError("DPAPI CryptUnprotectData failed")

    result = ctypes.string_at(blob_out.pbData, blob_out.cbData).decode("utf-8")
    ctypes.windll.kernel32.LocalFree(blob_out.pbData)
    return result


def get_github_token() -> str:
    """Get GitHub token from env var or CEAISuite settings."""
    env = os.environ.get("GITHUB_TOKEN")
    if env:
        return env
    settings_path = Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "settings.json"
    if settings_path.exists():
        data = json.loads(settings_path.read_text())
        enc = data.get("EncryptedGitHubToken")
        if enc:
            return dpapi_decrypt(enc)
    raise RuntimeError("No GitHub token found. Set GITHUB_TOKEN env var.")


# ── Copilot token exchange ──────────────────────────────────────────────
import urllib.request, urllib.error

COPILOT_HEADERS = {
    "Editor-Version": "vscode/1.95.3",
    "Editor-Plugin-Version": "copilot/1.246.0",
    "Copilot-Integration-Id": "vscode-chat",
    "User-Agent": "GitHubCopilotChat/0.22.4",
}

def get_copilot_token(github_token: str) -> str:
    """Exchange GitHub OAuth token for a Copilot session token."""
    req = urllib.request.Request(
        "https://api.github.com/copilot_internal/v2/token",
        headers={
            "Authorization": f"token {github_token}",
            **COPILOT_HEADERS,
        },
    )
    with urllib.request.urlopen(req) as resp:
        data = json.loads(resp.read())
    return data["token"]


# ── Test runner ─────────────────────────────────────────────────────────
from openai import OpenAI

SIMPLE_TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "list_processes",
            "description": "List running processes on the system.",
            "parameters": {"type": "object", "properties": {}, "required": []},
        },
    },
    {
        "type": "function",
        "function": {
            "name": "read_memory",
            "description": "Read a value from process memory.",
            "parameters": {
                "type": "object",
                "properties": {
                    "pid": {"type": "integer", "description": "Process ID"},
                    "address": {"type": "string", "description": "Hex address"},
                },
                "required": ["pid", "address"],
            },
        },
    },
]


def run_test(name: str, func):
    """Run a test and print result."""
    print(f"\n{'='*60}")
    print(f"TEST: {name}")
    print(f"{'='*60}")
    try:
        result = func()
        print(f"  PASS: {result}")
        return True
    except Exception as e:
        print(f"  FAIL: {e}")
        if hasattr(e, "response"):
            try:
                print(f"  Status: {e.response.status_code}")
                print(f"  Body: {e.response.text[:500]}")
            except:
                pass
        return False


def main():
    print("Copilot API Test Suite")
    print("=" * 60)

    github_token = get_github_token()
    print(f"GitHub token: {github_token[:8]}...{github_token[-4:]}")

    copilot_token = get_copilot_token(github_token)
    print(f"Copilot token: {copilot_token[:20]}...")

    results = {}

    for model in ["gpt-4o", "claude-sonnet-4.6"]:
        client = OpenAI(
            api_key=copilot_token,
            base_url="https://api.githubcopilot.com",
            default_headers=COPILOT_HEADERS,
        )

        # TEST 1: Simple chat, no tools
        def test_simple(m=model):
            resp = client.chat.completions.create(
                model=m,
                messages=[{"role": "user", "content": "Say hello in 5 words."}],
                max_tokens=50,
            )
            return f"[{m}] {resp.choices[0].message.content}"

        results[f"{model}/simple"] = run_test(f"Simple chat ({model})", test_simple)

        # TEST 2: Tools defined, no call expected
        def test_tools_defined(m=model):
            resp = client.chat.completions.create(
                model=m,
                messages=[{"role": "user", "content": "Say hello in 5 words. Do NOT use any tools."}],
                tools=SIMPLE_TOOLS,
                max_tokens=50,
            )
            return f"[{m}] {resp.choices[0].message.content}"

        results[f"{model}/tools-defined"] = run_test(f"Tools defined, no call ({model})", test_tools_defined)

        # TEST 3: Full tool call + result round-trip
        def test_tool_result(m=model):
            resp1 = client.chat.completions.create(
                model=m,
                messages=[{"role": "user", "content": "List the running processes."}],
                tools=SIMPLE_TOOLS,
                max_tokens=200,
            )
            msg1 = resp1.choices[0].message
            print(f"  Step 1 finish_reason: {resp1.choices[0].finish_reason}")
            print(f"  Step 1 tool_calls: {len(msg1.tool_calls or [])}")

            if not msg1.tool_calls:
                return f"[{m}] Model didn't call tools — skipping tool result test"

            tc = msg1.tool_calls[0]
            print(f"  Tool call ID: {tc.id}")
            print(f"  Tool function: {tc.function.name}")

            messages = [
                {"role": "user", "content": "List the running processes."},
                msg1.model_dump(),
                {
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "content": "PID=1234 notepad.exe x64\nPID=5678 explorer.exe x64\nPID=9012 chrome.exe x64",
                },
            ]
            resp2 = client.chat.completions.create(
                model=m,
                messages=messages,
                tools=SIMPLE_TOOLS,
                max_tokens=200,
            )
            return f"[{m}] {resp2.choices[0].message.content[:100]}"

        results[f"{model}/tool-result"] = run_test(f"Tool call + result ({model})", test_tool_result)

        # TEST 4: Streaming simple
        def test_streaming(m=model):
            stream = client.chat.completions.create(
                model=m,
                messages=[{"role": "user", "content": "Say hello in 5 words."}],
                max_tokens=50,
                stream=True,
            )
            text = ""
            for chunk in stream:
                delta = chunk.choices[0].delta if chunk.choices else None
                if delta and delta.content:
                    text += delta.content
            return f"[{m}] streamed: {text}"

        results[f"{model}/streaming"] = run_test(f"Streaming ({model})", test_streaming)

        # TEST 5: Streaming with tool result in history
        def test_streaming_tool_result(m=model):
            resp1 = client.chat.completions.create(
                model=m,
                messages=[{"role": "user", "content": "List the running processes."}],
                tools=SIMPLE_TOOLS,
                max_tokens=200,
            )
            msg1 = resp1.choices[0].message
            if not msg1.tool_calls:
                return f"[{m}] Model didn't call tools — skipping"

            tc = msg1.tool_calls[0]
            messages = [
                {"role": "user", "content": "List the running processes."},
                msg1.model_dump(),
                {
                    "role": "tool",
                    "tool_call_id": tc.id,
                    "content": "PID=1234 notepad.exe x64\nPID=5678 explorer.exe x64",
                },
            ]
            stream = client.chat.completions.create(
                model=m,
                messages=messages,
                tools=SIMPLE_TOOLS,
                max_tokens=200,
                stream=True,
            )
            text = ""
            for chunk in stream:
                delta = chunk.choices[0].delta if chunk.choices else None
                if delta and delta.content:
                    text += delta.content
            return f"[{m}] streamed after tool: {text[:100]}"

        results[f"{model}/streaming-tool-result"] = run_test(
            f"Streaming + tool result ({model})", test_streaming_tool_result
        )

        # TEST 6: Assistant content=null (explicit) with tool_calls
        def test_null_content(m=model):
            resp1 = client.chat.completions.create(
                model=m,
                messages=[{"role": "user", "content": "List the running processes."}],
                tools=SIMPLE_TOOLS,
                max_tokens=200,
            )
            msg1 = resp1.choices[0].message
            if not msg1.tool_calls:
                return f"[{m}] Model didn't call tools — skipping"

            tc = msg1.tool_calls[0]
            assistant_msg = {
                "role": "assistant",
                "content": None,
                "tool_calls": [{
                    "id": tc.id,
                    "type": "function",
                    "function": {"name": tc.function.name, "arguments": tc.function.arguments},
                }],
            }
            messages = [
                {"role": "user", "content": "List the running processes."},
                assistant_msg,
                {"role": "tool", "tool_call_id": tc.id, "content": "PID=1234 notepad.exe x64"},
            ]
            resp2 = client.chat.completions.create(
                model=m,
                messages=messages,
                tools=SIMPLE_TOOLS,
                max_tokens=200,
            )
            return f"[{m}] null content: {resp2.choices[0].message.content[:100]}"

        results[f"{model}/null-assistant-content"] = run_test(
            f"Assistant content=null ({model})", test_null_content
        )

    # TEST 7: 100 tool definitions
    def test_many_tools():
        many_tools = [
            {
                "type": "function",
                "function": {
                    "name": f"tool_{i:03d}",
                    "description": f"Test tool number {i} that does something useful.",
                    "parameters": {
                        "type": "object",
                        "properties": {"input": {"type": "string", "description": "Input value"}},
                        "required": ["input"],
                    },
                },
            }
            for i in range(100)
        ]
        resp = client.chat.completions.create(
            model="claude-sonnet-4.6",
            messages=[{"role": "user", "content": "Say hello in 5 words. Do NOT call any tools."}],
            tools=many_tools,
            max_tokens=50,
        )
        return f"100 tools: {resp.choices[0].message.content}"

    results["many-tools/100"] = run_test("100 tool definitions", test_many_tools)

    # TEST 8: Large tool result (5KB, similar to skill content)
    def test_large_result():
        resp1 = client.chat.completions.create(
            model="claude-sonnet-4.6",
            messages=[{"role": "user", "content": "List the running processes."}],
            tools=SIMPLE_TOOLS,
            max_tokens=200,
        )
        msg1 = resp1.choices[0].message
        if not msg1.tool_calls:
            return "Model didn't call tools — skipping"
        tc = msg1.tool_calls[0]
        big = "# Large Tool Result\n\n" + ("This is a detailed process listing.\n" * 150)
        messages = [
            {"role": "user", "content": "List the running processes."},
            {"role": "assistant", "content": None, "tool_calls": [
                {"id": tc.id, "type": "function",
                 "function": {"name": tc.function.name, "arguments": tc.function.arguments}}
            ]},
            {"role": "tool", "tool_call_id": tc.id, "content": big},
        ]
        resp2 = client.chat.completions.create(
            model="claude-sonnet-4.6",
            messages=messages,
            tools=SIMPLE_TOOLS,
            max_tokens=200,
        )
        return f"5KB result: {resp2.choices[0].message.content[:100]}"

    results["large-tool-result"] = run_test("Large tool result (5KB)", test_large_result)

    # TEST 9: Force Claude tool call with tool_choice
    def test_claude_forced_tool():
        resp1 = client.chat.completions.create(
            model="claude-sonnet-4.6",
            messages=[{"role": "user", "content": "List the running processes on this system."}],
            tools=SIMPLE_TOOLS,
            tool_choice={"type": "function", "function": {"name": "list_processes"}},
            max_tokens=200,
        )
        msg1 = resp1.choices[0].message
        print(f"  finish_reason: {resp1.choices[0].finish_reason}")
        print(f"  tool_calls: {len(msg1.tool_calls or [])}")
        if not msg1.tool_calls:
            return "Claude still didn't call tools even with tool_choice"

        tc = msg1.tool_calls[0]
        print(f"  Tool call ID: {tc.id}")

        messages = [
            {"role": "user", "content": "List the running processes on this system."},
            msg1.model_dump(),
            {"role": "tool", "tool_call_id": tc.id,
             "content": "PID=1234 notepad.exe x64\nPID=5678 explorer.exe x64"},
        ]
        resp2 = client.chat.completions.create(
            model="claude-sonnet-4.6",
            messages=messages,
            tools=SIMPLE_TOOLS,
            max_tokens=200,
        )
        return f"Claude tool result: {resp2.choices[0].message.content[:100]}"

    results["claude/forced-tool-result"] = run_test(
        "Claude forced tool call + result", test_claude_forced_tool
    )

    # TEST 10: Replay the exact failed request from our .NET app
    failed_path = Path(os.environ["LOCALAPPDATA"]) / "CEAISuite" / "logs" / "failed-request-102405-143.json"
    if failed_path.exists():
        def test_replay_failed():
            payload = json.loads(failed_path.read_text())
            # Send it exactly as-is via the REST API (bypass SDK serialization)
            import urllib.request
            body = json.dumps(payload).encode()
            req = urllib.request.Request(
                "https://api.githubcopilot.com/chat/completions",
                data=body,
                headers={
                    "Authorization": f"Bearer {copilot_token}",
                    "Content-Type": "application/json",
                    **COPILOT_HEADERS,
                },
                method="POST",
            )
            try:
                with urllib.request.urlopen(req) as resp:
                    data = json.loads(resp.read())
                return f"Replayed OK! {data['choices'][0]['message']['content'][:100]}"
            except urllib.error.HTTPError as e:
                body = e.read().decode()
                return f"HTTP {e.code}: {body[:300]}"

        results["replay-failed-request"] = run_test(
            "Replay exact .NET failed request", test_replay_failed
        )

    # ── Summary ─────────────────────────────────────────────────────────
    print(f"\n{'='*60}")
    print("SUMMARY")
    print(f"{'='*60}")
    passed = sum(1 for v in results.values() if v)
    total = len(results)
    for name, ok in results.items():
        print(f"  {'PASS' if ok else 'FAIL'} {name}")
    print(f"\n{passed}/{total} passed")


if __name__ == "__main__":
    main()
