"""Test: does streaming + tool results work on Copilot?"""
import json, os, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
from copilot_api_test import get_github_token, get_copilot_token, COPILOT_HEADERS
from openai import OpenAI

copilot_token = get_copilot_token(get_github_token())
client = OpenAI(
    api_key=copilot_token,
    base_url="https://api.githubcopilot.com",
    default_headers=COPILOT_HEADERS,
)

TOOLS = [
    {"type": "function", "function": {
        "name": "list_processes",
        "description": "List running processes.",
        "parameters": {"type": "object", "properties": {}, "required": []},
    }},
]

# Step 1: Non-streaming request that triggers tool call
print("=== Step 1: Get tool call (non-streaming) ===")
resp1 = client.chat.completions.create(
    model="claude-sonnet-4.6",
    messages=[{"role": "user", "content": "List the running processes."}],
    tools=TOOLS,
    tool_choice={"type": "function", "function": {"name": "list_processes"}},
    max_tokens=200,
)
msg1 = resp1.choices[0].message
tc = msg1.tool_calls[0]
print("Tool call ID:", tc.id)
print("Function:", tc.function.name)

# Build conversation with tool result
messages = [
    {"role": "user", "content": "List the running processes."},
    {"role": "assistant", "content": None, "tool_calls": [
        {"id": tc.id, "type": "function",
         "function": {"name": tc.function.name, "arguments": tc.function.arguments}}
    ]},
    {"role": "tool", "tool_call_id": tc.id,
     "content": "PID=1234 notepad.exe x64\nPID=5678 explorer.exe x64"},
]

# Step 2a: Non-streaming with tool result
print("\n=== Step 2a: Tool result — NON-STREAMING ===")
try:
    resp2 = client.chat.completions.create(
        model="claude-sonnet-4.6", messages=messages, tools=TOOLS, max_tokens=200,
        stream=False,
    )
    print("PASS:", resp2.choices[0].message.content[:100])
except Exception as e:
    print("FAIL:", e)

# Step 2b: Streaming with tool result
print("\n=== Step 2b: Tool result — STREAMING ===")
try:
    stream = client.chat.completions.create(
        model="claude-sonnet-4.6", messages=messages, tools=TOOLS, max_tokens=200,
        stream=True,
    )
    text = ""
    for chunk in stream:
        delta = chunk.choices[0].delta if chunk.choices else None
        if delta and delta.content:
            text += delta.content
    print("PASS:", text[:100])
except Exception as e:
    print("FAIL:", e)

# Step 2c: Streaming WITHOUT tools defined (tool result in history but no tools param)
print("\n=== Step 2c: Tool result in history, streaming, NO tools param ===")
try:
    stream = client.chat.completions.create(
        model="claude-sonnet-4.6", messages=messages, max_tokens=200,
        stream=True,
    )
    text = ""
    for chunk in stream:
        delta = chunk.choices[0].delta if chunk.choices else None
        if delta and delta.content:
            text += delta.content
    print("PASS:", text[:100])
except Exception as e:
    print("FAIL:", e)

# Also test GPT-4o for comparison
print("\n=== GPT-4o: Tool result — STREAMING ===")
try:
    resp_g = client.chat.completions.create(
        model="gpt-4o",
        messages=[{"role": "user", "content": "List the running processes."}],
        tools=TOOLS,
        tool_choice={"type": "function", "function": {"name": "list_processes"}},
        max_tokens=200,
    )
    tc_g = resp_g.choices[0].message.tool_calls[0]
    msgs_g = [
        {"role": "user", "content": "List the running processes."},
        {"role": "assistant", "content": None, "tool_calls": [
            {"id": tc_g.id, "type": "function",
             "function": {"name": tc_g.function.name, "arguments": tc_g.function.arguments}}
        ]},
        {"role": "tool", "tool_call_id": tc_g.id,
         "content": "PID=1234 notepad.exe x64\nPID=5678 explorer.exe x64"},
    ]
    stream = client.chat.completions.create(
        model="gpt-4o", messages=msgs_g, tools=TOOLS, max_tokens=200, stream=True,
    )
    text = ""
    for chunk in stream:
        delta = chunk.choices[0].delta if chunk.choices else None
        if delta and delta.content:
            text += delta.content
    print("PASS:", text[:100])
except Exception as e:
    print("FAIL:", e)
