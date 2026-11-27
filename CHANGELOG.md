# Changelog — Sutra (Revit, AutoCAD & Civil 3D MCP by SCIO DC)

## v1.3.0 (2026-11-26)

Clean-room expansion adding structural view planning, web-ready 3D mesh export (`glTF 2.0 / .glb` & `geometry_json`), 2-layer raster Image → BIM workflows, Roslyn in-memory C# execution under `UnsafeCode`, and per-material takeoff/CRUD parity across Revit 2023–2027.

### Highlights & New Capabilities

- **Section Box from Elements & Structural View Planner (Phase 1):**
  - `scio_view_crop`: added `set_section_box_from_elements` (`element_ids` + `padding_mm`) with post-commit read-back verification (`verified_section_box_active`, `verified_section_box`).
  - `scio_plan_views`: added read-only `element_views` planner with 3 organisation-neutral presets (`isometric_by_host`, `beam_sections`, `grid_elevations_sheets`) and pure math helpers in `PlanimetryMathKit`.
- **Lightweight 3D Mesh Export for Web Viewers (Phase 2):**
  - `scio_export_mesh` + `MeshExportKit`: exports triangulated 3D meshes via Revit `CustomExporter` (`IExportContext`) as binary **glTF 2.0 (`.glb`)** or compact **`geometry_json` (`scio.mesh/1`)** with scope filters (`view_id`, `level_name`, `categories`, `element_ids`), explicit `omitted_elements`, atomic disk write, and post-commit header/SHA-256 verification.
- **2-Layer Raster Image → BIM (Phase 3):**
  - `scio_manage_images`: typed Revit raster management (`list`, `import`, `transform`, `reload`, `delete`) with `dry_run: true` default and post-commit read-back.
  - `scio_plan_from_image` + `ImageVectorizeKit`: host-resident read-only vectorization in `Scio.Server` (Otsu thresholding, Zhang-Suen thinning, Douglas-Peucker simplification, parallel-face wall pairing) enforcing explicit scale calibration (`mm_per_pixel` or two calibration points + `known_distance_mm`).
- **Owner-Gated Dynamic C# Execution (Phase 4):**
  - `Scio.Revit.CSharp` (`scio_execute_csharp`): in-memory Roslyn compilation + collectible `AssemblyLoadContext` on Revit 2025–2027 (`.NET 8/10`), explicit `unsupported_runtime_net48` refusal on Revit 2023–2024 (`.NET Framework 4.8`), disabled by default (`PermissionProfile.UnsafeCode`), blocked inside `CodeModeTool`, `host_verified: false`, zero HTTP/CORS.
- **Material Takeoff, CRUD, Templates & Geometry Diff Parity (Phase 5):**
  - `scio_material_takeoff`: per-material area (m²) and volume (m³) takeoff from compound layers and optional paint faces with organisation-neutral `unit_rates` costing and JSON/CSV export.
  - `scio_create_schedule`: added `schedule_kind: "material_takeoff"` (`ViewSchedule.CreateMaterialTakeoff`, including `Multi-Category`), `group_by`, and `itemized`.
  - `scio_manage_materials`: added `action: "update"` (single or batch rename, `description`, `cost`, `keynote`, `mark`, `comments`, `rgb`, `transparency`) and `action: "assign"`.
  - `scio_view_templates`: added `action: "create_from_view"` (`view.CreateViewTemplate()`).
  - `scio_model_scan` & `scio_model_diff`: added `bbox_mm` snapshots and `geometry_changed` detection with configurable `geometry_tolerance_mm`.
- **IFC Delivery Fix (`scio_deliver_ifc`):** added `export_rooms` (`SpaceBoundaryLevel` + `ExportRoomsInView=true`, default `true`), `export_revit_property_sets` (`ExportInternalRevitPropertySets=true`, default `true` so `Comments` and `Mark` export to IFC), `export_ifc_common_property_sets`, `view_id`, and post-export `ifc_space_count` / `ifc_property_set_count` verification.
- **Claude Desktop (Microsoft Store / MSIX) MCP Fix:** `scripts/configure-mcp-clients.ps1` now configures every `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\claude_desktop_config.json` (creating parent folders if needed) alongside `%APPDATA%\Claude`, and `SessionRegistry` now publishes and discovers Revit session beacons under `%USERPROFILE%\.scio\v4\sessions` (unaffected by MSIX `%LOCALAPPDATA%` virtualization) with backward-compatible fallback.
- **In-App Update Alerts (`SutraUpdateChecker` & `UpdateAvailableDialog`):** non-blocking 24h-cached GitHub Releases check on Revit startup, manual "Buscar actualizaciones" button in `AboutDialog`, and cached `update_available` metadata in `scio_health`.

## v1.2.0 (2026-11-26)

Unified multi-host release: a single Windows Setup installer now deploys **Sutra** across **Autodesk Revit (2023–2027)**, **AutoCAD (2023–2026)**, and **Civil 3D (2023–2026)**, adding parametric automatic drafting across all disciplines and a local Design Criteria Manager (`SUTRACRITERIA`).

### Highlights & New Capabilities

- **Unified Multi-Host Windows Setup & MCP Auto-Configuration (`build/sutra-revit-mcp.iss`, `scripts/configure-mcp-clients.ps1`, `build/pack-release.ps1`):** automatically detects installed Revit (`2023–2027`) and AutoCAD / Civil 3D (`2023–2026`), deploys both the Revit add-in and `SutraCad.bundle` (`Platform="AutoCAD|AutoCAD*|Civil3D"`), enforces safe process checks (`Revit.exe` + `acad.exe`), performs clean in-place upgrades from prior Sutra / legacy SCIO installs, and auto-configures/updates the `scio-revit` MCP entry across detected AI clients (Claude Desktop, Claude Code, Antigravity, Gemini CLI, Cursor, Windsurf, VS Code, Codex CLI) without touching other MCP servers.
- **Scan-to-BIM (`scio_scan_to_bim` + `ScanToBimRules`):** read-only `.rcp`/`.rcs` point-cloud extraction in Revit (`inspect_clouds`, `detect_levels`, `slice_plan`, `fit_primitive` with USIBD LOA classification).
- **AutoCAD & Civil 3D Automatic Drafting Suite (`Scio.Cad` + `Scio.Server`):**
  - `scio_cad_draft`: atomic drafting engine (`setup_styles`, `draw_batch` with lines, polylines with bulge/width, rectangles, arcs, circles, hatches, MText, callouts, linear/aligned/radial dimensions, block inserts with attributes, native tables, and `list_blocks`).
  - `scio_cad_arch_plan`: parametric architectural floor-plan drafting (`draw_grid`, `draw_walls`, `draw_openings`, `draw_room_tags` with area calculation and room schedule table).
  - `scio_cad_detail`: construction detail generator (`wall_section`, `layer_buildup`, `spec_legend`) with hatches, non-overlapping callouts, dimensions, and normative citations.
  - `scio_cad_mep`: MEP automatic drafting (`draw_pipe_run` with slopes & computed invert elevations, `draw_duct_run`, `draw_electrical_layout` with circuits/fixtures/cable trays, and `draw_mep_legend`).
  - `scio_cad_struct`: structural drafting (`draw_column_section`, `draw_beam_section`, `draw_footing_detail`, `draw_rebar_schedule`).
  - `scio_cad_civil_draft`: roads & civil drafting (`draw_road_section`, `draw_culvert_section`, `draw_profile_grid`) working in both plain AutoCAD and Civil 3D.
- **Design Criteria Manager (`SUTRACRITERIA` Ribbon button + `scio_cad_criteria`):** load, validate, activate, and query structured `sutra.design_criteria.v1` JSON packs (exported from Gemini Notebook / technical books) stored in `%USERPROFILE%\.scio\criteria\`, with built-in min/max compliance checks and book/section/page citations.
- **Multi-Host Routing & AutoCAD/Civil 3D Core (`Scio.Cad`):** `scio_cad_context`, `scio_cad_censo`, `scio_cad_write`, `scio_cad_cleanup`, `scio_cad_point_cloud`, `scio_c3d_inspect`, `scio_c3d_write`, `scio_c3d_export`, and `scio_target` host pinning (`revit`, `autocad`, `civil3d`, `auto`).

## v1.1.0 (2026-11-26)

Windows Setup installer with automatic in-place upgrades and unified assembly versioning across all components.

### Highlights & Improvements

- **Windows Setup installer with in-place upgrades (`build/sutra-revit-mcp.iss`):** detects previous Sutra (`v1.0.x`) and legacy SCIO Revit MCP installations, stops idle `scio-revit-mcp.exe` processes, cleans stale server/add-in folders and legacy manifests (`Scio.addin`, `.previous`, `.installing`), and updates every installed Autodesk Revit version (`2023–2027`) cleanly in place.
- **Unified version reporting (`RevitBridgeApp`, `McpStdioHost`, `TelemetryTool`):** `scio_health`, `scio_target`, MCP `initialize`, **Acerca de Sutra**, and opt-in telemetry now read the canonical assembly version directly from `Directory.Build.props` (`1.1.0`) instead of hardcoded placeholders.
- **Reproducible release packager (`build/pack-release.ps1`):** runs unit/IPC tests, publishes the self-contained `win-x64` MCP server, compiles per-year Revit `2023–2027` add-ins, builds `sutra-revit-mcp-1.1.0-setup.exe` + portable `.zip`, and writes `SHA256SUMS.txt`.

## v1.0.1 (2026-11-26)

Patch release fixing tool discovery, missing tool packs, and Revit Ribbon UX.

### Fixes & Improvements

- **Lazy remote catalog hydration (`Scio.Server`):** `DispatchAsync`, `scio_code_mode`, `scio_list_tools`, `scio_describe_tool`, `scio_target`, and `resources/read` now automatically hydrate the remote Revit tool catalog whenever a live Revit session appears or changes — eliminating the stale 20-tool state when the MCP client starts before Revit.
- **All 10 tool packs enabled (`ToolPackResolver`):** registered missing `modeling` (32+ tools), `interop` (12), `cad_ifc` (8), and `iso19650` (6) packs in `Dependencies`, unlocking all **168 visible tools** (including `scio_create_elements` and `scio_create_family`).
- **Bilingual ES/EN search in `scio_list_tools` & `scio.search()`:** wired `BimSearchIndex` with added furniture (`mobiliario`/`furniture`), level (`nivel`/`level`), and placement (`colocar`/`place`) synonym groups.
- **Revit Ribbon icons, branded dialogs & About window (`Scio.Revit.Ui`):** added crisp WPF vector icons (`16×16` and `32×32`) for every button, upgraded status/pause/Python dialogs to `ScioDialog` (Light/Dark Revit theme aware), and added a dedicated **Acerca de Sutra** window.
- **Installer (`install.ps1`):** cleans legacy manifests/payloads and builds + deploys per Revit year (2023–2027).

## v1.0.0 (2026-11-24)

First public **Clean-Room** release of **Sutra — Revit MCP** (`mpomav/sutra-revit-mcp`), supporting **Autodesk Revit 2023–2027** across **9 modular domains** and **130 verified tools**.

### Highlights

- **100% Clean-Room Modular Architecture:** built from scratch with `Scio.Abstractions`, `Scio.Ipc`, `Scio.Server`, `Scio.Revit.Host`, `Scio.Revit.Sdk`, `Scio.Revit.Ui`, `Scio.Revit.Python`, and 9 domain modules (`CoreRead`, `ScioNative`, `Modeling`, `Documentation`, `AuditQa`, `Mep`, `Structure`, `CadIfc`, `IsoInterop`). Verified in [PROVENANCE.md](PROVENANCE.md).
- **Hardened Local Code Mode by default (`SCIO_SCHEMA_MODE=codemode`):** exposes 4 meta-tools with on-demand `scio_list_tools` / `scio_describe_tool` and a fail-closed JS sandbox (`read_only: true` default, strict step/memory/timeout ceilings, zero raw Python access).
- **Local-first SQLite analytics:** `scio_query_local`, `scio_report`, `scio_batch_audit`, `scio_progress_takeoff`, and `scio_suggest_workflow` run locally in `Scio.Server` without blocking Revit's UI thread.
- **Strictly Opt-In Telemetry (`scio_telemetry`):** OFF by default (`telemetry_opt_in: false`). When explicitly enabled by the owner, records anonymous tool counts, timings, schema mode, 2-letter OS region, and version — never IPs, file paths, or model content.
- **Sutra Ribbon & Identity:** native **Sutra** tab in Revit 2023–2027 (`net48`, `net8.0-windows`, `net10.0-windows`), official Nepali abstract icon, and CLI epigraphs (`--marco`, `--scio`, `--filosofia`).
