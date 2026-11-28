# Security Policy — Sutra (Revit & CAD MCP)

Sutra is designed from the ground up as a **100% local-first** Model Context Protocol bridge for Autodesk Revit (2023–2027) and AutoCAD / Civil 3D (2023–2026). Your BIM/CAD models never leave your workstation.

## Security Architecture

- **Local-only IPC:** Communication between `Scio.Server` (MCP stdio) and desktop add-ins uses Windows Named Pipes restricted to the current Windows user (`PipeOptions.CurrentUserOnly` on .NET 8/10; explicit per-user SID `PipeSecurity` ACL on .NET Framework 4.8), with bounded concurrency (`MaxConcurrentClients = 32`) and strict JSON deserialization limits (`MaxDepth = 64`, `TypeNameHandling = None`).
- **Permission Profiles:**
  - `ReadOnly`: Inspection, queries, audits, and takeoffs only. Blocks all model mutations.
  - `SafeWrite` *(default)*: Typed Sutra tools with `dry_run: true` preview, transaction rollback, warning capture, and post-commit read-back verification. Blocks arbitrary Python and C# execution.
  - `UnsafeCode` *(opt-in per session)*: Authorizes `scio_execute_python` (IronPython) and `scio_execute_csharp` (Roslyn collectible `AssemblyLoadContext`). Requires explicit owner confirmation via modal checkbox in the Revit Ribbon; blocks UNC paths and non-script file extensions.
- **Hardened Code Mode Sandbox:** `scio_code_mode` executes a zero-dependency JS subset inside `Scio.Server` with `read_only: true` by default, zero filesystem/network/.NET reflection access, hard AST step/timeout/output budgets, and explicit blocks on `scio_execute_python`, `scio_execute_csharp`, `scio_promote_script`, and `scio_call_tool`.
- **SQLite Read-Only Analytics:** `scio_query_local` opens `scio_local.db` with `SQLITE_OPEN_READONLY`, blocks multi-statements/comments/DDL/DML/PRAGMA/ATTACH and null bytes, and enforces a progress-handler timeout to prevent recursive-CTE CPU exhaustion.
- **Strict Outbound Controls:**
  - **Telemetry (`scio_telemetry`):** OFF by default (`telemetry_opt_in: false`). When opted in, sends only anonymous tool counts/timings to `https://*.scio.pe` (localhost, raw IPs, non-HTTPS, and arbitrary hosts are rejected).
  - **Update checks:** Read-only GitHub Releases API check (24h cached, disableable via `SCIO_DISABLE_UPDATE_CHECK=1`). Release/installer URLs are strictly validated against `https://github.com/mpomav/sutra-revit-mcp/` and GitHub release asset hosts before opening.

## Reporting a Vulnerability

Please report security issues privately to **SCIO DC** via GitHub Security Advisories on `mpomav/sutra-revit-mcp` or by email to `seguridad@scio.pe` (do not open public issues for undisclosed vulnerabilities). Include reproduction steps, affected version, and Revit/AutoCAD year.
