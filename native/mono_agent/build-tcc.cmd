@echo off
REM Build mono_agent.dll using TinyCC — no Visual Studio Build Tools required.
REM TCC: https://bellard.org/tcc/ (single exe, no installer needed)

if "%TCC%"=="" set TCC=tcc

%TCC% -shared -o mono_agent.dll mono_agent.c -lkernel32 -ladvapi32
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo BUILD OK: mono_agent.dll (TCC)
dir mono_agent.dll
