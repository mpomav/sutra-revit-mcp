# Provenance & Clean-Room Authorship Ledger

**Product:** Sutra — Revit MCP v1.3.0 (by SCIO DC)  
**Copyright Owner:** Marco Poma / SCIO DC (Grupo POVA, Lima, Peru)  
**License:** Apache License, Version 2.0  

## Authorship Policy

Every source file in this repository is either:

1. **Authored from scratch** for **Sutra v1.0.0** against functional specifications and the public Autodesk Revit API, or
2. **Migrated exclusively from original SCIO DC source files** authored by SCIO DC.

No third-party MCP bridge source code is copied or redistributed in this repository. Third-party runtime dependencies are consumed strictly as NuGet packages and documented in `THIRD-PARTY-NOTICES.md`.

> **Audit script scope (`build/audit-clean-room.ps1`):** provides automated hygiene checks (forbidden third-party identifier scan, `[ScioTool]` inventory count, and optional SHA-256 comparison against a local legacy checkout when present). It is a repository hygiene check, not a legal certification.

## Module Provenance (130 tools total)

| Module / Assembly | Origin & Scope | Status |
| --- | --- | --- |
| `Scio.Abstractions` | Original (SCIO DC): `[ScioTool]` attributes, reflection schema builder, verification envelopes, core helpers | Done |
| `Scio.Ipc` | Original (SCIO DC): length-prefixed Named Pipes hardened with `PipeOptions.CurrentUserOnly` (`.NET 8/10`) and per-user SID `PipeSecurity` ACL (`.NET 4.8`) | Done |
| `Scio.Server` | Original (SCIO DC): MCP stdio host (`codemode` default / `facade` / `full`), hardened `CodeModeEngine`, local SQLite analytics, workflow recommender, and opt-in `scio_telemetry` — **12 server tools** | Done |
| `Scio.Revit.Sdk` | Original (SCIO DC): `RevitContext`, `IdCompat`, `TransactionPipeline`, `RevitModuleLoader`, `RevitElementKit` | Done |
| `Scio.Revit.Host` | Original (SCIO DC): FIFO dispatcher, `_scio_catalog`, module auto-loader, Ribbon hooks | Done |
| `Scio.Revit.Python` | Original (SCIO DC): owner-gated isolated IronPython escape hatch (`scio_execute_python`) | Done |
| `Scio.Revit.CSharp` | Original (SCIO DC): owner-gated in-memory Roslyn C# execution in collectible `AssemblyLoadContext` on Revit 2025+ (`scio_execute_csharp`) | Done |
| `Scio.Revit.Ui` | Original (SCIO DC): native **Sutra** ribbon (`ScioRibbon`), `ScioTheme`, `ScioDialog`, `SchemaForm`, `ClaudeHandoff` | Done |
| `Scio.Module.CoreRead` | Original (SCIO DC) — **24 tools** (session, read, navigation, diff, params, delete, plan, copy, `scio_template_kit`, `scio_measure_geometry`) | Done |
| `Scio.Module.ScioNative` | Original (SCIO DC) — **9 native commands** (`Context`, `ExplainFinding`, `ModelCleanup`, `NamingCheck`, `PresyncCheck`, `RecipeDispatcher`, `RfiGenerator`, `RuleApply`, `VisualQa`) | Done |
| `Scio.Module.Modeling` | Original (SCIO DC) — **22 tools** (elements, geometry, curtain, slab shape, framing, railing, families, catalog, model config, `scio_join_geometry`, `scio_split_by_level`, `scio_wall_finish`) | Done |
| `Scio.Module.Documentation` | Original (SCIO DC) — **19 tools** (views, sheets, schedules, dimensions, tags, detail, legends, callouts, revision clouds) | Done |
| `Scio.Module.AuditQa` | Original (SCIO DC) — **13 tools** (model audit, clash, clearance, quantity & per-material takeoff, warnings, params, links, coordination issues, `scio_audit_support`, `scio_purge_unused`, `scio_code_check`, `scio_change_survey`) | Done |
| `Scio.Module.Mep` | Original (SCIO DC) — **7 tools** (pipe, duct, conduit/tray, systems, connectors, slope check, electrical) | Done |
| `Scio.Module.Structure` | Original (SCIO DC) — **7 tools** (structural inventory, rebar inspect/create/cover, connections, analytical audit, PDF schedule) | Done |
| `Scio.Module.CadIfc` | Original (SCIO DC) — **11 tools** (CAD inspect/link/walls/grids, IFC deliver/audit, IDS check, georeference, `scio_scan_to_bim`, `scio_export_mesh`, `scio_manage_images`) | Done |
| `Scio.Module.IsoInterop` | Original (SCIO DC) — **12 tools** (ISO 19650 project context, containers, CDE, `scio.exchange/0`, Excel/CSV, budget, Power BI, COBie, NWC/FBX, delivery) | Done |
