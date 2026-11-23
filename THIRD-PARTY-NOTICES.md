# Third-Party Notices — SCIO Revit MCP v4

SCIO Revit MCP v4 is open-source software licensed under the **Apache License, Version 2.0** (Copyright 2026 Marco Poma / SCIO DC).

The packages below are consumed via NuGet and governed by their own licenses:

| Component | Version | License | Purpose |
| --- | --- | --- | --- |
| Newtonsoft.Json | 13.0.3 | MIT | JSON serialization for MCP and IPC envelopes |
| IronPython | 3.4.2 | Apache-2.0 | Isolated Python escape hatch inside Revit |
| System.Text.Encoding.CodePages | 8.0.0 | MIT | Codepage 1252 support for IronPython on .NET 8 targets |

## Referenced at build time only (never redistributed)

- **Autodesk Revit API** (`RevitAPI.dll`, `RevitAPIUI.dll`) is referenced with `Private=false` and is never copied or redistributed. Autodesk and Revit are trademarks of Autodesk, Inc.
