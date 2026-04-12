@echo off
REM Build veh_agent.dll — requires Visual Studio Build Tools (cl.exe)
REM Run from a "Developer Command Prompt for VS" or "x64 Native Tools Command Prompt"

cl.exe /LD /O2 /W4 /WX veh_agent.c /link /DLL /OUT:veh_agent.dll kernel32.lib
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo BUILD OK: veh_agent.dll
dir veh_agent.dll
