@echo off
REM Build veh_agent.dll using TinyCC — no Visual Studio Build Tools required.
REM TCC: https://bellard.org/tcc/ (single exe, no installer needed)
REM
REM For x64 (default):
REM   build-tcc.cmd
REM
REM For x86:
REM   set TCC=path\to\i386-win32-tcc.exe
REM   build-tcc.cmd

if "%TCC%"=="" set TCC=tcc

%TCC% -shared -o veh_agent.dll veh_agent.c -lkernel32
if errorlevel 1 (
    echo BUILD FAILED
    exit /b 1
)
echo BUILD OK: veh_agent.dll (TCC)
dir veh_agent.dll
