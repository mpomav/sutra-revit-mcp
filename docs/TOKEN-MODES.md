# Guía de modos de contexto / AI Context Modes — Sutra v1.0.0

**Sutra v1.0.0** reúne **130 herramientas Clean-Room** (118 en los 9 módulos de Revit + `Scio.Revit.Python`, más 12 herramientas de servidor/meta). En modo `full`, cuando Revit está conectado e hidrata el catálogo completo por IPC (`_scio_catalog`), anunciar más de 100 esquemas a la vez ocupa contexto inicial y provoca **degradación de selección de herramientas** (*tool selection degradation*).

**Sutra** resuelve esto arrancando por defecto en **`codemode`**, cuyo tamaño inicial es **constante (3 114 bytes)** sin importar cuántas herramientas tenga cargadas Revit:

![Comparación de carga inicial de contexto de Sutra v1.0.0](assets/token-comparison.svg)

## Medición real sobre `scio-revit-mcp.exe` (`v1.0.0`)

Medido directamente por stdio (`initialize` + `tools/list`, UTF-8):

| Modo (`SCIO_SCHEMA_MODE`) | Herramientas en `tools/list` | `initialize` | `tools/list` | Total UTF-8 | Tokens estimados* | Tipo de dato |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| **`codemode`** *(por defecto)* | 4 | 211 B | 2 903 B | **3 114 B** | **~778 – 944** | Medido (binario v1.0.0) |
| **`facade`** | 4 | 211 B | 1 567 B | **1 778 B** | **~444 – 539** | Medido (binario v1.0.0) |
| **`full`** *(sin Revit abierto)* | 9 | 211 B | 4 265 B | **4 476 B** | **~1 119 – 1 356** | Medido (binario v1.0.0) |
| **`full`** *(Revit conectado, 127 tools)* | 127 | 211 B | ~60 000 B | **~60 KB** | **~15 000 – 18 200** | Estimado (~474 B/tool) |

*\*Rango estimado entre 4.0 bytes/token (texto estándar) y 3.3 bytes/token (JSON denso).*

---

## Cómo funciona `Code Mode` sin adivinar firmas

1. Expone **`scio_list_tools`** y **`scio_describe_tool`** para consultar el catálogo y el esquema JSON exacto bajo demanda antes de invocar.
2. Ejecuta scripts cortos en un **sandbox JS local sin dependencias** (`CodeModeEngine`, `read_only: true` por defecto, límites estrictos de memoria, pasos y timeout).
3. **Solo despacha herramientas tipadas de Sutra** (bloqueando `scio_call_tool`, `scio_execute_python` y llamadas recursivas).

---

## Cómo cambiar de modo

- **Variable de entorno (prioridad máxima):** `SCIO_SCHEMA_MODE=codemode` | `facade` | `full`.
- **Archivo local:** `%USERPROFILE%\.scio\settings.json` con `"schema_mode": "codemode"` (o `"facade"`, `"full"`).
