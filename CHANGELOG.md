# Changelog — Sutra (Revit MCP by SCIO DC)

## v1.0.0 (2026-11-24)

First public **Clean-Room** release of **Sutra — Revit MCP** (`mpomav/sutra-revit-mcp`), supporting **Autodesk Revit 2023–2027** across **9 modular domains** and **124 verified tools**.

### Highlights

- **100% Clean-Room Modular Architecture:** built from scratch with `Scio.Abstractions`, `Scio.Ipc`, `Scio.Server`, `Scio.Revit.Host`, `Scio.Revit.Sdk`, `Scio.Revit.Ui`, `Scio.Revit.Python`, and 9 domain modules (`CoreRead`, `ScioNative`, `Modeling`, `Documentation`, `AuditQa`, `Mep`, `Structure`, `CadIfc`, `IsoInterop`). Verified in [PROVENANCE.md](PROVENANCE.md).
- **Hardened Local Code Mode by default (`SCIO_SCHEMA_MODE=codemode`):** exposes 4 meta-tools with on-demand `scio_list_tools` / `scio_describe_tool` and a fail-closed JS sandbox (`read_only: true` default, strict step/memory/timeout ceilings, zero raw Python access).
- **Local-first SQLite analytics:** `scio_query_local`, `scio_report`, `scio_batch_audit`, `scio_progress_takeoff`, and `scio_suggest_workflow` run locally in `Scio.Server` without blocking Revit's UI thread.
- **Strictly Opt-In Telemetry (`scio_telemetry`):** OFF by default (`telemetry_opt_in: false`). When explicitly enabled by the owner, records anonymous tool counts, timings, schema mode, 2-letter OS region, and version — never IPs, file paths, or model content.
- **Sutra Ribbon & Identity:** native **Sutra** tab in Revit 2023–2027 (`net48`, `net8.0-windows`, `net10.0-windows`), official Nepali abstract icon, and CLI epigraphs (`--marco`, `--scio`, `--filosofia`).
