# Provenance & Clean-Room Authorship Ledger

**Product:** SCIO Revit MCP v4.0  
**Copyright Owner:** Marco Poma / SCIO DC (Grupo POVA, Lima, Peru)  
**License:** Apache License, Version 2.0  

## Clean-Room Policy

Every source file in this repository is either:

1. **Authored from scratch** for SCIO Revit MCP v4.0 against functional specifications and the public Autodesk Revit API, or
2. **Migrated exclusively from original SCIO DC source files** authored from scratch by SCIO DC (audited to contain zero third-party derivative code).

No third-party MCP bridge source code is copied, renamed, or derived in this repository. Third-party runtime dependencies are consumed strictly as NuGet packages and documented in `THIRD-PARTY-NOTICES.md`.

## Module Provenance

| Module / Assembly | Origin | Wave | Status |
| --- | --- | --- | --- |
| `Scio.Abstractions` | Clean-room original (SCIO DC) + SCIO Core engines (`ScioHtmlTheme`, `RunRefResolver`, `LocalPlanStore`, `ProjectClassificationLookup`, `RuleMatchEngine`) | Wave 1 | Done |
| `Scio.Ipc` | Clean-room original (SCIO DC) | Wave 1 | Done |
| `Scio.Server` | Clean-room original (SCIO DC) + SCIO local tools (`CodeMode`, `SqliteIngestor`, `BimSearchIndex`, `QueryLocal`, `Report`, `BatchAudit`, `ProgressTakeoff`, `SuggestWorkflow`) + dynamic IPC catalog hydration | Wave 1–2 | Done |
| `Scio.Revit.Sdk` | Clean-room original (SCIO DC): `RevitContext`, `TransactionPipeline`, `RevitModuleLoader`, `RevitElementKit` | Wave 1–2 | Done |
| `Scio.Revit.Host` | Clean-room original (SCIO DC): FIFO dispatcher, `_scio_catalog`, module auto-loader, Ribbon hooks | Wave 1–2 | Done |
| `Scio.Revit.Python` | Clean-room original (SCIO DC) + 9 SCIO Python recipes + `IRevitModule` | Wave 1–2 | Done |
| `Scio.Revit.Ui` | Original SCIO DC UI (`ScioTheme`, `ScioDialog`, `SchemaForm`, `ClaudeHandoff`, DM Sans fonts, SCIO icons) + clean-room `ScioRibbon` | Wave 1–2 | Done |
| `Scio.Module.CoreRead` | Clean-room original (SCIO DC) — 22 core session, read, navigation, diff, parameter, delete, plan & copy tools | Wave 2 | Done |
| `Scio.Module.ScioNative` | 11 verified 100% SCIO-authored Revit commands (`Context`, `ExplainFinding`, `ModelCleanup`, `NamingCheck`, `PresyncCheck`, `RecipeDispatcher`, `RfiGenerator`, `RuleApply`, `ScioOutput`, `SelectionResolver`, `VisualQa`) + clean-room `NativeShims` | Wave 2 | Done |
| `Scio.Module.Modeling` | Clean-room original (SCIO DC) | Wave 3 | Planned |
| `Scio.Module.Documentation` | Clean-room original (SCIO DC) | Wave 4 | Planned |
| `Scio.Module.AuditQa` | Clean-room original (SCIO DC) | Wave 5 | Planned |
| `Scio.Module.Mep` | Clean-room original (SCIO DC) | Wave 5 | Planned |
| `Scio.Module.Structure` | Clean-room original (SCIO DC) | Wave 6 | Planned |
| `Scio.Module.CadIfc` | Clean-room original (SCIO DC) | Wave 7 | Planned |
| `Scio.Module.IsoInterop` | Clean-room original (SCIO DC) | Wave 7 | Planned |
