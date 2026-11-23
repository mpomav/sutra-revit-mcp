# SCIO Revit MCP v4

Modular Model Context Protocol (MCP) bridge connecting AI clients (Claude, Codex, Cursor, ChatGPT, and any MCP-compatible client) to Autodesk Revit 2023–2027.

Developed from scratch by **SCIO DC (Grupo POVA)** — *modelamos como quien ya construyó*.

## Architecture

- **`Scio.Abstractions`** — Attribute-driven tool contracts (`[ScioTool]`), reflection-based JSON Schema generation, and standardized execution/verification envelopes.
- **`Scio.Ipc`** — Length-prefixed Named Pipes IPC transport with cancellation, timeouts, and FIFO queuing.
- **`Scio.Server`** — Clean MCP stdio host supporting `full`, `facade`, and `codemode` discovery modes, plus local SQLite analytics and Code Mode sandbox.
- **`Scio.Revit.Sdk` / `Scio.Revit.Host`** — Multi-target Revit add-in (`net48` for Revit 2023–2024, `net8.0-windows` for Revit 2025–2026, `net10.0-windows` for Revit 2027) with transactional dry-run/verify pipeline and modular domain auto-registration.
- **`Scio.Revit.Python`** — Isolated IronPython escape hatch and SCIO automation recipes.
- **`Scio.Revit.Ui`** — Native SCIO Revit UI (Copilot pane, connection status, client connector).

## License

Licensed under the [Apache License, Version 2.0](LICENSE). Copyright 2026 Marco Poma / SCIO DC.
