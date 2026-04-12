Place the pre-compiled `veh_agent.dll` here.

Build it from `native/veh_agent/veh_agent.c` using:
- The GitHub Actions workflow `build-veh-agent.yml` (automatic on push to native/veh_agent/)
- Locally: `cl.exe /LD /O2 /W4 veh_agent.c /link /DLL /OUT:veh_agent.dll` from a VS Developer Command Prompt

The .dll is copied to the output directory at build time via the Engine.Windows .csproj.
