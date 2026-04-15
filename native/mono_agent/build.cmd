@echo off
REM Build mono_agent.dll — requires Visual Studio Build Tools (cl.exe)
REM Run from a "Developer Command Prompt for VS" or "x64 Native Tools Command Prompt"
REM
REM Alternative: build-tcc.cmd uses TinyCC (no VS required).

cl.exe /LD /O2 /W4 /WX mono_agent.c /link /DLL /OUT:mono_agent.dll kernel32.lib advapi32.lib
if errorlevel 1 (
    echo BUILD FAILED (x64)
    exit /b 1
)
echo BUILD OK: mono_agent.dll (x64)
dir mono_agent.dll
