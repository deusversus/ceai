@echo off
REM Build veh_agent.dll — requires Visual Studio Build Tools (cl.exe)
REM Run from a "Developer Command Prompt for VS" or "x64 Native Tools Command Prompt"
REM For x86 build, use "x86 Native Tools Command Prompt" instead.
REM
REM Alternative: build-tcc.cmd uses TinyCC (no VS required).

cl.exe /LD /O2 /W4 /WX veh_agent.c /link /DLL /OUT:veh_agent.dll kernel32.lib
if errorlevel 1 (
    echo BUILD FAILED (x64)
    exit /b 1
)
echo BUILD OK: veh_agent.dll (x64)
dir veh_agent.dll
