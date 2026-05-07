# AudioScript

## Inheritance Rule

- Always read and follow [D:\Projects\AGENTS.md](D:\Projects\AGENTS.md) first.

## Build And Run Rules

- If the project is a Windows desktop application and the task may result in any build, rebuild, publish, or similar compilation step, always close the running app first.
- If the task includes UI changes, apply the same rule.
- Do not start the build until the app has fully exited.
- For routine development requests like "build debug", "build and restart", or "start debug exe", use FAST-BUILD by default: build only `AudioScript.csproj` in Debug with analyzers disabled using MSBuild `/t:Build /p:Configuration=Debug /p:RunAnalyzers=false /m`.
- Do not run FULL-BUILD by default. FULL-BUILD is required only for MS Store package/submission workflows (solution/package build paths) or when explicitly requested by the user.
- After the task completes successfully, always run the appropriate build for the request (FAST-BUILD by default; FULL-BUILD only in the allowed cases), then restart the same app automatically if it was running before the build.
- When starting or restarting the app, launch it directly from that project’s Debug executable output (not via tool-host wrappers).
- If the task fails, do not restart the app.
