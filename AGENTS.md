# Sutra — Revit MCP (Guide for Agents)

You are in the **Sutra — Revit MCP** repository (`c:\PROG\sutra`, remote `sutra`: `https://github.com/mpomav/sutra-revit-mcp.git`).

- **Product name:** **Sutra** (full form: **Sutra — Revit MCP**). Always use `Sutra` in prose and user-facing docs. Code identifiers (`Scio.*`, `scio_*`) are kept for wire/API compatibility.
- **Architecture:** Clean-room, modular, local-first MCP bridge for Autodesk Revit 2023–2027 (`src/Modules/`, `src/Scio.Abstractions`, `src/Scio.Ipc`, `src/Scio.Revit.Host`, `src/Scio.Revit.Python`, `src/Scio.Revit.Sdk`, `src/Scio.Revit.Ui`, `src/Scio.Server`).
- **Build & verify:**
  - `pwsh -File build/build-all.ps1` — compiles `Scio.Server`, all Revit modules across installed years (2023–2027), and runs `tests/Scio.Core.Tests`.
  - `pwsh -File build/audit-clean-room.ps1` — clean-room audit and `[ScioTool]` inventory check.
  - `pwsh -File build/pack-release.ps1` — packages release artifacts and Windows Setup (`build/sutra-revit-mcp.iss`).
- **Core principles:**
  - Strict clean-room implementation (never copy external decompiled code).
  - Organisation-neutral by design (standards, classifications, and naming rules arrive as call-time arguments or requirement sets, never hardcoded).
  - Local-first security (stdio + named pipe IPC, never expose an unauthenticated HTTP/CORS server).
  - Transactional honesty (`dry_run` + confirmation token + post-commit read-back for typed writes; explicit `host_verified: false` for dynamic code execution).
