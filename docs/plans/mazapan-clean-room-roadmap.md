# Plan Maestro Clean-Room: Insumos de Mazapan (Teseracto v2.6.1) → Sutra

**Principio rector:** Este plan se formula **100% clean-room** a partir de especificaciones funcionales y de la auditoría directa del código de **Sutra** (`c:\PROG\sutra`, v1.1.0, 130 herramientas). No se copia código externo, cumpliendo `AGENTS.md` y `PROVENANCE.md`.

---

## 1. Los 6 Ejes donde Sutra supera estructuralmente a Mazapan

1. **Seguridad y transporte:** Stdio + Windows Named Pipes (`PipeOptions.CurrentUserOnly` en `.NET 8/10`, ACL por SID en `.NET 4.8`), cero HTTP/CORS abierto, 4 perfiles de permisos (`read_only`, `safe_write`, `full_write`, `unsafe_code`), ejecución dinámica apagada por defecto.
2. **Arquitectura de dos procesos + contrato verificado:** `Scio.Server` independiente + `Scio.Revit.Host` acoplados por hash de contrato; herramientas host-resident sin bloquear Revit; cola FIFO y validación de `target_document`.
3. **Honestidad transaccional:** `dry_run: true` por defecto (`rehearse → commit → re-read`), verificación post-commit y separación explícita `host_verified: false` (`SelfReportedVerified`) para código dinámico.
4. **Primitivas componibles y agnósticas:** organización-neutral; separa planners read-only (`scio_plan_structure`, `scio_plan_views`, `scio_plan_annotations`) de ejecutores atómicos.
5. **Control de contexto LLM:** `Code Mode` por defecto (`scio_list_tools` / `scio_describe_tool` + sandbox JS endurecido) + modos `facade` y `full`.
6. **Compatibilidad multi-versión real (Revit 2023–2027):** compilación real por año (`net48`, `net8.0-windows`, `net10.0-windows`) con rechazos explícitos cuando una API no existe en un año.

---

## 2. Roadmap Clean-Room en 5 Fases

### Fase 1 — `set_section_box_from_elements` + Planner de Vistas Estructurales
- **Extender `scio_view_crop`** (`Scio.Module.Documentation/ViewCropAndGraphicsCommands.cs`):
  - Nueva operación `set_section_box_from_elements` (`view_id`, `element_ids`, `padding_mm`, `dry_run`) con cálculo de caja envolvente en coordenadas del modelo y re-lectura post-commit de `IsSectionBoxActive` y `GetSectionBox()`.
- **Extender `scio_plan_views`** (`Scio.Module.Documentation/PlanimetryAndAnnotationPlannerCommands.cs`):
  - Nueva operación read-only `element_views` con 3 presets parametrizables (`isometric_by_host`, `beam_sections`, `grid_elevations_sheets`) que devuelven planes listos para `scio_create_views` + `scio_view_crop` + `scio_manage_sheets`, sin nombres ni estándares hardcodeados.

### Fase 2 — Exportación de Geometría 3D para Visor Web (`scio_export_mesh`)
- Nuevo comando `scio_export_mesh` (`Scio.Module.CadIfc`):
  - Formatos `glb` (glTF 2.0 binario) y `geometry_json` (mallas trianguladas indexadas + metadatos por elemento).
  - Basado en `CustomExporter` (`IExportContext`), filtros por vista/nivel/categoría/IDs y `lod`, reporte explícito de `omitted_elements` y verificación post-commit (firma mágica, tamaño, SHA-256).

### Fase 3 — Imágenes Raster + Pipeline Imagen → BIM en 2 Capas
- `scio_manage_images` (`Scio.Module.CadIfc`): `list`, `import`, `transform`, `reload`, `delete` con `dry_run: true` y re-lectura post-commit.
- `scio_plan_from_image` (host-resident read-only en `Scio.Server`): calibración explícita (`mm_per_pixel` o `known_distance_mm`), Otsu + Zhang-Suen + Douglas-Peucker → payload revisable para `scio_create_detail_lines` o `scio_create_elements`.

### Fase 4 — `scio_execute_csharp` (Opcional, bajo `PermissionProfile.UnsafeCode`)
- Nuevo proyecto `src/Scio.Revit.CSharp`: Roslyn en memoria + `AssemblyLoadContext(isCollectible: true)` en Revit 2025–2027 (`.NET 8/10`), rechazo explícito `unsupported_runtime_net48` en Revit 2023–2024 (`.NET 4.8`), bloqueado en `CodeModeEngine`, `host_verified: false`, cero HTTP/CORS.

### Fase 5 — Paridad interna en Sutra v1.1.0
- `scio_material_takeoff` (metrado por capa/material, organización-neutral).
- `scio_create_schedule` con `schedule_kind: "material_takeoff"`.
- `scio_manage_materials` con `action: "update"` y `"assign"`.
- `scio_view_templates` con `action: "create_from_view"`.
- `scio_model_scan` + `scio_model_diff` con `bbox_mm` para detectar movimientos geométricos.

---

## 3. Verificación por Fase
- `pwsh -File build/build-all.ps1`
- `pwsh -File build/audit-clean-room.ps1`
- Tests unitarios en `tests/Scio.Core.Tests`
