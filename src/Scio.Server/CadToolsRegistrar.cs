using Newtonsoft.Json.Linq;
using Scio.Abstractions;

namespace Scio.Server
{
    /// <summary>
    /// Registers AutoCAD / Civil 3D (Scio.Cad) remote tools routed via CadPipeClient.
    /// </summary>
    internal static class CadToolsRegistrar
    {
        public static void RegisterAll(ServerToolRegistry registry)
        {
            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_context",
                RemoteCommand = "cad_context",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = true,
                Description = "Live AutoCAD / Civil 3D drawing context: active DWG, units, coordinate system, space/layout, current layer, selection summary, XREFs, and Civil 3D status.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""include_layers"": { ""type"": ""boolean"", ""default"": false },
    ""include_xrefs"": { ""type"": ""boolean"", ""default"": true },
    ""max_items"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_censo",
                RemoteCommand = "cad_censo",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = true,
                Description = "In-process AutoCAD/Civil 3D entity census grouped by type, layer, block and space. Streams full CSV to %USERPROFILE%\\.scio\\salidas\\ for scio_query_local SQLite.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""space"": { ""type"": ""string"", ""enum"": [""model"", ""paper"", ""all""], ""default"": ""model"" },
    ""layer_filter"": { ""type"": ""string"" },
    ""include_block_attributes"": { ""type"": ""boolean"", ""default"": false },
    ""max_groups"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 50 }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_c3d_inspect",
                RemoteCommand = "c3d_inspect",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = true,
                Description = "Inspect live Civil 3D domain objects in the active DWG: summary, TIN/Grid surfaces, alignments, COGO points, pipe networks, and corridors.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""summary"", ""surfaces"", ""alignments"", ""cogo_points"", ""pipe_networks"", ""corridors""], ""default"": ""summary"" },
    ""name_filter"": { ""type"": ""string"" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_write",
                RemoteCommand = "cad_write",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Destructive = true,
                Description = "Transactional AutoCAD/Civil 3D write (ensure_layers, create_entities, or delete_entities) with dry_run=true default, confirmation_token, Transaction commit, and post-commit Handle re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""ensure_layers"", ""create_entities"", ""delete_entities""] },
    ""layers"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""name""], ""properties"": { ""name"": { ""type"": ""string"" }, ""color_aci"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 }, ""is_off"": { ""type"": ""boolean"" }, ""is_frozen"": { ""type"": ""boolean"" }, ""is_locked"": { ""type"": ""boolean"" } }, ""additionalProperties"": false } },
    ""entities"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""kind""], ""properties"": { ""kind"": { ""type"": ""string"", ""enum"": [""line"", ""polyline"", ""circle"", ""text"", ""point""] }, ""layer"": { ""type"": ""string"" }, ""start"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }, ""end"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }, ""points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } }, ""closed"": { ""type"": ""boolean"", ""default"": false }, ""center"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }, ""radius"": { ""type"": ""number"" }, ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 }, ""text"": { ""type"": ""string"" }, ""height"": { ""type"": ""number"" } }, ""additionalProperties"": false } },
    ""handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_c3d_write",
                RemoteCommand = "c3d_write",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Transactional Civil 3D creation/editing (create_cogo_points, create_tin_surface, add_surface_data, create_alignment, edit_pipe_network) with dry_run=true default, confirmation_token, Transaction commit, and post-commit re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""create_cogo_points"", ""create_tin_surface"", ""add_surface_data"", ""create_alignment"", ""edit_pipe_network""] },
    ""name"": { ""type"": ""string"" },
    ""layer"": { ""type"": ""string"" },
    ""points"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""easting"", ""northing""], ""properties"": { ""easting"": { ""type"": ""number"" }, ""northing"": { ""type"": ""number"" }, ""elevation"": { ""type"": ""number"", ""default"": 0 }, ""description"": { ""type"": ""string"" }, ""name"": { ""type"": ""string"" } }, ""additionalProperties"": false } },
    ""surface_name"": { ""type"": ""string"" },
    ""breakline_handles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""polyline_handle"": { ""type"": ""string"" },
    ""alignment_points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } },
    ""pipe_edits"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""required"": [""handle""], ""properties"": { ""handle"": { ""type"": ""string"" }, ""start_invert"": { ""type"": ""number"" }, ""end_invert"": { ""type"": ""number"" }, ""rim_elevation"": { ""type"": ""number"" }, ""sump_elevation"": { ""type"": ""number"" }, ""description"": { ""type"": ""string"" } }, ""additionalProperties"": false } },
    ""rebuild"": { ""type"": ""boolean"", ""default"": true },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_c3d_export",
                RemoteCommand = "c3d_export",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = true,
                OpenWorld = true,
                Description = "Generate and export Civil 3D engineering data (cogo_csv, surfaces_csv, alignments_csv, pipe_network_csv, surface_grid_csv, landxml) into %USERPROFILE%\\.scio\\salidas\\ with automatic SQLite ingestion for scio_query_local.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""cogo_csv"", ""surfaces_csv"", ""alignments_csv"", ""pipe_network_csv"", ""surface_grid_csv"", ""landxml""] },
    ""name_filter"": { ""type"": ""string"" },
    ""surface_name"": { ""type"": ""string"" },
    ""station_interval"": { ""type"": ""number"", ""minimum"": 0.5, ""maximum"": 1000, ""default"": 20 },
    ""grid_spacing"": { ""type"": ""number"", ""minimum"": 0.5, ""maximum"": 1000, ""default"": 10 },
    ""max_rows"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100000, ""default"": 20000 },
    ""include_surfaces"": { ""type"": ""boolean"", ""default"": true },
    ""include_alignments"": { ""type"": ""boolean"", ""default"": true },
    ""include_cogo_points"": { ""type"": ""boolean"", ""default"": true },
    ""include_pipe_networks"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_cleanup",
                RemoteCommand = "cad_cleanup",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Destructive = true,
                Description = "Audit and clean an AutoCAD/Civil 3D drawing: flatten to Z=0, PEDIT/join lines and arcs into polylines, sort entities by layer, detect/fix large blocks (explode/purge), and remove degenerate zero-length/empty entities. Defaults to dry_run=true with confirmation_token and post-commit verification.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""flatten"": { ""type"": ""boolean"", ""default"": true },
    ""pedit_join"": { ""type"": ""boolean"", ""default"": true },
    ""join_tolerance"": { ""type"": ""number"", ""minimum"": 0, ""maximum"": 10, ""default"": 0.001 },
    ""sort_by_layer"": { ""type"": ""boolean"", ""default"": true },
    ""normalize_bylayer"": { ""type"": ""boolean"", ""default"": true },
    ""layer_map"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"" } },
    ""fix_large_blocks"": { ""type"": ""boolean"", ""default"": true },
    ""max_block_entities"": { ""type"": ""integer"", ""minimum"": 10, ""maximum"": 100000, ""default"": 500 },
    ""max_block_extent"": { ""type"": ""number"", ""minimum"": 1, ""maximum"": 10000000, ""default"": 10000 },
    ""large_block_action"": { ""type"": ""string"", ""enum"": [""explode"", ""report_only""], ""default"": ""explode"" },
    ""purge_unused"": { ""type"": ""boolean"", ""default"": true },
    ""remove_degenerate"": { ""type"": ""boolean"", ""default"": true },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_point_cloud",
                RemoteCommand = "cad_point_cloud",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "AutoCAD / Civil 3D Scan-to-BIM point cloud (.rcp/.rcs) tool: inspect linked PointCloudEx entities, attach .rcp/.rcs to ModelSpace, apply/clear rectangular crop_box boundaries, or stylize (rgb, elevation, intensity, normal, classification, object_color). Mutations default to dry_run=true with confirmation_token and post-commit Handle re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""inspect"", ""attach"", ""crop_box"", ""stylize""], ""default"": ""inspect"" },
    ""target_document"": { ""type"": ""string"" },
    ""path"": { ""type"": ""string"", ""description"": ""attach: absolute path to .rcp or .rcs file."" },
    ""location"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""scale"": { ""type"": ""number"", ""default"": 1 },
    ""rotation_deg"": { ""type"": ""number"", ""default"": 0 },
    ""layer"": { ""type"": ""string"" },
    ""handle"": { ""type"": ""string"", ""description"": ""crop_box/stylize: hex handle of PointCloudEx."" },
    ""min"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""max"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""clear"": { ""type"": ""boolean"", ""default"": false },
    ""invert"": { ""type"": ""boolean"", ""default"": false },
    ""replace_existing"": { ""type"": ""boolean"", ""default"": true },
    ""stylization"": { ""type"": ""string"", ""enum"": [""rgb"", ""elevation"", ""intensity"", ""normal"", ""classification"", ""object_color""] },
    ""color_scheme"": { ""type"": ""string"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_draft",
                RemoteCommand = "cad_draft",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Atomic AutoCAD / Civil 3D automatic drafting engine: setup_styles (layers, linetypes, lineweights, TextStyle, DimStyle), draw_batch (line, rectangle, polyline with bulge/width, circle, arc, hatch, mtext, callout, dim_linear, dim_aligned, dim_radial, insert_block with attributes, native table), or list_blocks. Mutations default to dry_run=true with confirmation_token and post-commit Handle re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""setup_styles"", ""draw_batch"", ""list_blocks""] },
    ""name_filter"": { ""type"": ""string"" },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 500, ""default"": 100 },
    ""dim_style"": { ""type"": ""string"" },
    ""layers"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""color_aci"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 },
          ""linetype"": { ""type"": ""string"" },
          ""lineweight_mm"": { ""type"": ""number"" },
          ""description"": { ""type"": ""string"" },
          ""is_plottable"": { ""type"": ""boolean"" }
        },
        ""additionalProperties"": false
      }
    },
    ""text_styles"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""font"": { ""type"": ""string"" },
          ""height"": { ""type"": ""number"" }
        },
        ""additionalProperties"": false
      }
    },
    ""dim_styles"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""text_height"": { ""type"": ""number"" },
          ""arrow_size"": { ""type"": ""number"" },
          ""overall_scale"": { ""type"": ""number"" },
          ""precision"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 8 }
        },
        ""additionalProperties"": false
      }
    },
    ""items"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""kind""],
        ""properties"": {
          ""kind"": { ""type"": ""string"", ""enum"": [""line"", ""rectangle"", ""polyline"", ""circle"", ""arc"", ""hatch"", ""mtext"", ""callout"", ""dim_linear"", ""dim_aligned"", ""dim_radial"", ""insert_block"", ""table""] },
          ""layer"": { ""type"": ""string"" },
          ""color_aci"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 },
          ""start"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""end"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""origin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""center"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } },
          ""bulges"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
          ""boundary"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } },
          ""closed"": { ""type"": ""boolean"" },
          ""width"": { ""type"": ""number"" },
          ""height"": { ""type"": ""number"" },
          ""constant_width"": { ""type"": ""number"" },
          ""radius"": { ""type"": ""number"" },
          ""start_angle_deg"": { ""type"": ""number"" },
          ""end_angle_deg"": { ""type"": ""number"" },
          ""pattern"": { ""type"": ""string"" },
          ""pattern_scale"": { ""type"": ""number"" },
          ""pattern_angle_deg"": { ""type"": ""number"" },
          ""text"": { ""type"": ""string"" },
          ""alignment"": { ""type"": ""string"" },
          ""rotation_deg"": { ""type"": ""number"" },
          ""background_mask"": { ""type"": ""boolean"" },
          ""arrow"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""landing"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""landing_length"": { ""type"": ""number"" },
          ""dim_line_point"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""chord_point"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""leader_length"": { ""type"": ""number"" },
          ""text_override"": { ""type"": ""string"" },
          ""block_name"": { ""type"": ""string"" },
          ""scale"": { ""type"": ""number"" },
          ""scale_x"": { ""type"": ""number"" },
          ""scale_y"": { ""type"": ""number"" },
          ""scale_z"": { ""type"": ""number"" },
          ""attributes"": { ""type"": ""object"" },
          ""title"": { ""type"": ""string"" },
          ""headers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
          ""rows"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } },
          ""col_widths"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } }
        },
        ""additionalProperties"": false
      }
    },
    ""criteria_ref"": { ""type"": ""object"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_arch_plan",
                RemoteCommand = "cad_arch_plan",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Parametric AutoCAD architectural floor-plan drafting: draw_grid (axes, bubbles, partial+total dimensions), draw_walls (double-line walls with optional hatch & dimensions), draw_openings (parametric doors with swing arc & windows with glazing/tags), and draw_room_tags (area calculation, level/finish tags, optional room schedule table). Defaults to dry_run=true with confirmation_token and post-commit verification.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""draw_grid"", ""draw_walls"", ""draw_openings"", ""draw_room_tags""] },
    ""origin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""x_spacings"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""y_spacings"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""x_labels"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""y_labels"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""overhang"": { ""type"": ""number"" },
    ""bubble_radius"": { ""type"": ""number"" },
    ""text_height"": { ""type"": ""number"" },
    ""include_dimensions"": { ""type"": ""boolean"" },
    ""grid_layer"": { ""type"": ""string"" },
    ""bubble_layer"": { ""type"": ""string"" },
    ""dim_layer"": { ""type"": ""string"" },
    ""wall_layer"": { ""type"": ""string"" },
    ""hatch_layer"": { ""type"": ""string"" },
    ""door_layer"": { ""type"": ""string"" },
    ""window_layer"": { ""type"": ""string"" },
    ""tag_layer"": { ""type"": ""string"" },
    ""default_thickness"": { ""type"": ""number"" },
    ""hatch_pattern"": { ""type"": ""string"" },
    ""hatch_scale"": { ""type"": ""number"" },
    ""walls"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""start"", ""end""],
        ""properties"": {
          ""start"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""end"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""thickness"": { ""type"": ""number"" },
          ""justification"": { ""type"": ""string"", ""enum"": [""center"", ""left"", ""right""] },
          ""layer"": { ""type"": ""string"" },
          ""hatch_pattern"": { ""type"": ""string"" }
        },
        ""additionalProperties"": false
      }
    },
    ""openings"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""kind"", ""position""],
        ""properties"": {
          ""kind"": { ""type"": ""string"", ""enum"": [""door"", ""window""] },
          ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""width"": { ""type"": ""number"" },
          ""wall_thickness"": { ""type"": ""number"" },
          ""rotation_deg"": { ""type"": ""number"" },
          ""swing"": { ""type"": ""string"", ""enum"": [""left"", ""right""] },
          ""tag"": { ""type"": ""string"" }
        },
        ""additionalProperties"": false
      }
    },
    ""rooms"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""polygon"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } },
          ""area_m2"": { ""type"": ""number"" },
          ""level_text"": { ""type"": ""string"" },
          ""finish"": { ""type"": ""string"" }
        },
        ""additionalProperties"": false
      }
    },
    ""include_schedule_table"": { ""type"": ""boolean"" },
    ""table_position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""table_title"": { ""type"": ""string"" },
    ""criteria_ref"": { ""type"": ""object"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_detail",
                RemoteCommand = "cad_detail",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Parametric AutoCAD construction-detail generator: wall_section (vertical multi-layer wall detail), layer_buildup (horizontal floor/roof/pavement assembly), and spec_legend (material hatch swatch legend). Automatically generates hatches, non-overlapping callouts, partial+total dimensions, titles, and normative/book citations. Defaults to dry_run=true with confirmation_token and post-commit verification.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""wall_section"", ""layer_buildup"", ""spec_legend""] },
    ""origin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""title"": { ""type"": ""string"" },
    ""scale_label"": { ""type"": ""string"" },
    ""section_height"": { ""type"": ""number"" },
    ""section_width"": { ""type"": ""number"" },
    ""text_height"": { ""type"": ""number"" },
    ""cut_layer"": { ""type"": ""string"" },
    ""hatch_layer"": { ""type"": ""string"" },
    ""dim_layer"": { ""type"": ""string"" },
    ""note_layer"": { ""type"": ""string"" },
    ""layers_stack"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name"", ""thickness""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""thickness"": { ""type"": ""number"" },
          ""spec"": { ""type"": ""string"" },
          ""hatch_pattern"": { ""type"": ""string"" },
          ""hatch_scale"": { ""type"": ""number"" },
          ""hatch_angle_deg"": { ""type"": ""number"" },
          ""color_aci"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 }
        },
        ""additionalProperties"": false
      }
    },
    ""items"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""name""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""description"": { ""type"": ""string"" },
          ""citation"": { ""type"": ""string"" },
          ""hatch_pattern"": { ""type"": ""string"" },
          ""hatch_scale"": { ""type"": ""number"" },
          ""color_aci"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 }
        },
        ""additionalProperties"": false
      }
    },
    ""citations"": {
      ""type"": ""array"",
      ""items"": {}
    },
    ""criteria_ref"": { ""type"": ""object"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_mep",
                RemoteCommand = "cad_mep",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Parametric AutoCAD MEP automatic drafting: draw_pipe_run (cold/hot water, sanitary, storm, fire, gas with flow arrows, fittings, slopes & computed invert elevations), draw_duct_run (double-line HVAC supply/return/exhaust/fresh_air with centerlines & size labels), draw_electrical_layout (circuits with arcs/wire ticks, panels/luminaires/outlets/switches/detectors, and runged cable trays), and draw_mep_legend. Defaults to dry_run=true with confirmation_token and post-commit Handle re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""draw_pipe_run"", ""draw_duct_run"", ""draw_electrical_layout"", ""draw_mep_legend""] },
    ""system"": { ""type"": ""string"" },
    ""layer"": { ""type"": ""string"" },
    ""center_layer"": { ""type"": ""string"" },
    ""note_layer"": { ""type"": ""string"" },
    ""circuit_layer"": { ""type"": ""string"" },
    ""fixture_layer"": { ""type"": ""string"" },
    ""tray_layer"": { ""type"": ""string"" },
    ""color_aci"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 },
    ""linetype"": { ""type"": ""string"" },
    ""points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } },
    ""diameter_label"": { ""type"": ""string"" },
    ""slope_percent"": { ""type"": ""number"" },
    ""start_elevation"": { ""type"": ""number"" },
    ""line_width"": { ""type"": ""number"" },
    ""width"": { ""type"": ""number"" },
    ""height"": { ""type"": ""number"" },
    ""size_label"": { ""type"": ""string"" },
    ""text_height"": { ""type"": ""number"" },
    ""flow_arrows"": { ""type"": ""boolean"" },
    ""draw_fittings"": { ""type"": ""boolean"" },
    ""include_centerline"": { ""type"": ""boolean"" },
    ""origin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""title"": { ""type"": ""string"" },
    ""circuits"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""points""],
        ""properties"": {
          ""points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } },
          ""bulge"": { ""type"": ""number"" },
          ""tag"": { ""type"": ""string"" },
          ""wire_spec"": { ""type"": ""string"" },
          ""wires"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 12 },
          ""layer"": { ""type"": ""string"" }
        },
        ""additionalProperties"": false
      }
    },
    ""fixtures"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""kind"", ""position""],
        ""properties"": {
          ""kind"": { ""type"": ""string"", ""enum"": [""panel"", ""luminaire"", ""outlet"", ""switch"", ""detector"", ""junction_box""] },
          ""position"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
          ""size"": { ""type"": ""number"" },
          ""width"": { ""type"": ""number"" },
          ""height"": { ""type"": ""number"" },
          ""tag"": { ""type"": ""string"" },
          ""layer"": { ""type"": ""string"" }
        },
        ""additionalProperties"": false
      }
    },
    ""trays"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""points""],
        ""properties"": {
          ""points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 } },
          ""width"": { ""type"": ""number"" },
          ""rung_spacing"": { ""type"": ""number"" },
          ""layer"": { ""type"": ""string"" }
        },
        ""additionalProperties"": false
      }
    },
    ""items"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""label""],
        ""properties"": {
          ""label"": { ""type"": ""string"" },
          ""spec"": { ""type"": ""string"" },
          ""citation"": { ""type"": ""string"" },
          ""layer"": { ""type"": ""string"" },
          ""color_aci"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 255 },
          ""linetype"": { ""type"": ""string"" }
        },
        ""additionalProperties"": false
      }
    },
    ""criteria_ref"": { ""type"": ""object"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_criteria",
                RemoteCommand = "cad_criteria",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Manage and query local Sutra design-criteria JSON packs (sutra.design_criteria.v1) exported from Gemini Notebook / books and loaded via the SUTRACRITERIA AutoCAD Ribbon button: list_packs, query (by discipline/category/tags/text with resolved book citations), get_assembly, validate_values (min/max compliance check), import_pack, and get_export_prompt.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""properties"": {
    ""operation"": { ""type"": ""string"", ""enum"": [""list_packs"", ""query"", ""get_assembly"", ""validate_values"", ""import_pack"", ""get_export_prompt""], ""default"": ""list_packs"" },
    ""discipline"": { ""type"": ""string"" },
    ""category"": { ""type"": ""string"" },
    ""text"": { ""type"": ""string"" },
    ""tags"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""include_assemblies"": { ""type"": ""boolean"", ""default"": true },
    ""include_cad_defaults"": { ""type"": ""boolean"", ""default"": false },
    ""active_only"": { ""type"": ""boolean"", ""default"": true },
    ""limit"": { ""type"": ""integer"", ""minimum"": 1, ""maximum"": 100, ""default"": 20 },
    ""assembly_id"": { ""type"": ""string"" },
    ""rule_id"": { ""type"": ""string"" },
    ""proposed_values"": { ""type"": ""object"" },
    ""file_path"": { ""type"": ""string"" },
    ""pack_json"": { ""type"": ""object"" },
    ""activate"": { ""type"": ""boolean"", ""default"": true }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_struct",
                RemoteCommand = "cad_struct",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Parametric AutoCAD structural drafting: draw_column_section (concrete hatch, stirrups with 135-deg hook, longitudinal bars, dims & callouts), draw_beam_section (top/bottom bars, stirrups, optional slab flange), draw_footing_detail (plan + elevation section with solado, mat rebar, Df & dims), and draw_rebar_schedule (native AutoCAD table). Supports normative citations and defaults to dry_run=true with confirmation_token and post-commit Handle re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""draw_column_section"", ""draw_beam_section"", ""draw_footing_detail"", ""draw_rebar_schedule""] },
    ""origin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""tag"": { ""type"": ""string"" },
    ""title"": { ""type"": ""string"" },
    ""scale_label"": { ""type"": ""string"" },
    ""width"": { ""type"": ""number"" },
    ""depth"": { ""type"": ""number"" },
    ""height"": { ""type"": ""number"" },
    ""cover"": { ""type"": ""number"" },
    ""bars_x"": { ""type"": ""integer"", ""minimum"": 2, ""maximum"": 20 },
    ""bars_y"": { ""type"": ""integer"", ""minimum"": 2, ""maximum"": 20 },
    ""top_bars_count"": { ""type"": ""integer"", ""minimum"": 2, ""maximum"": 20 },
    ""bottom_bars_count"": { ""type"": ""integer"", ""minimum"": 2, ""maximum"": 20 },
    ""bar_diameter_mm"": { ""type"": ""number"" },
    ""slab_thickness"": { ""type"": ""number"" },
    ""bar_label"": { ""type"": ""string"" },
    ""top_bar_label"": { ""type"": ""string"" },
    ""bottom_bar_label"": { ""type"": ""string"" },
    ""stirrup_label"": { ""type"": ""string"" },
    ""concrete_fc"": { ""type"": ""string"" },
    ""width_x"": { ""type"": ""number"" },
    ""width_y"": { ""type"": ""number"" },
    ""footing_height"": { ""type"": ""number"" },
    ""col_width"": { ""type"": ""number"" },
    ""col_depth"": { ""type"": ""number"" },
    ""df_depth"": { ""type"": ""number"" },
    ""subbase_thickness"": { ""type"": ""number"" },
    ""rebar_x_label"": { ""type"": ""string"" },
    ""rebar_y_label"": { ""type"": ""string"" },
    ""text_height"": { ""type"": ""number"" },
    ""concrete_layer"": { ""type"": ""string"" },
    ""rebar_layer"": { ""type"": ""string"" },
    ""stirrup_layer"": { ""type"": ""string"" },
    ""dim_layer"": { ""type"": ""string"" },
    ""note_layer"": { ""type"": ""string"" },
    ""headers"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } },
    ""rows"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } },
    ""col_widths"": { ""type"": ""array"", ""items"": { ""type"": ""number"" } },
    ""citations"": { ""type"": ""array"", ""items"": {} },
    ""criteria_ref"": { ""type"": ""object"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });

            registry.RegisterRemoteTool(new ToolDescriptor
            {
                Name = "scio_cad_civil_draft",
                RemoteCommand = "cad_civil_draft",
                TargetHost = "cad",
                Pack = "cad",
                ReadOnly = false,
                Description = "Parametric AutoCAD / Civil 3D roads & civil engineering drafting: draw_road_section (typical cross-section with lanes, crossfall, shoulders, ditches/sidewalks, hatched pavement layers, CL & dimensions), draw_culvert_section (box_culvert or pipe_culvert with solado & dims), and draw_profile_grid (longitudinal profile grid with stationing, vertical exaggeration, EG and FG curves). Supports normative citations and defaults to dry_run=true with confirmation_token and post-commit Handle re-read.",
                InputSchema = JObject.Parse(@"{
  ""type"": ""object"",
  ""required"": [""operation""],
  ""properties"": {
    ""target_document"": { ""type"": ""string"" },
    ""operation"": { ""type"": ""string"", ""enum"": [""draw_road_section"", ""draw_culvert_section"", ""draw_profile_grid""] },
    ""origin"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 3 },
    ""title"": { ""type"": ""string"" },
    ""scale_label"": { ""type"": ""string"" },
    ""text_height"": { ""type"": ""number"" },
    ""lanes_left"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 8 },
    ""lanes_right"": { ""type"": ""integer"", ""minimum"": 0, ""maximum"": 8 },
    ""lane_width"": { ""type"": ""number"" },
    ""crossfall_percent"": { ""type"": ""number"" },
    ""shoulder_left"": { ""type"": ""number"" },
    ""shoulder_right"": { ""type"": ""number"" },
    ""shoulder_slope_percent"": { ""type"": ""number"" },
    ""ditch_width"": { ""type"": ""number"" },
    ""ditch_depth"": { ""type"": ""number"" },
    ""sidewalk_left"": { ""type"": ""number"" },
    ""sidewalk_right"": { ""type"": ""number"" },
    ""curb_height"": { ""type"": ""number"" },
    ""pavement_layers"": {
      ""type"": ""array"",
      ""items"": {
        ""type"": ""object"",
        ""required"": [""thickness""],
        ""properties"": {
          ""name"": { ""type"": ""string"" },
          ""thickness"": { ""type"": ""number"" },
          ""hatch_pattern"": { ""type"": ""string"" },
          ""hatch_scale"": { ""type"": ""number"" }
        },
        ""additionalProperties"": false
      }
    },
    ""kind"": { ""type"": ""string"", ""enum"": [""box_culvert"", ""pipe_culvert""] },
    ""inner_width"": { ""type"": ""number"" },
    ""inner_height"": { ""type"": ""number"" },
    ""wall_thickness"": { ""type"": ""number"" },
    ""subbase_thickness"": { ""type"": ""number"" },
    ""start_station"": { ""type"": ""number"" },
    ""end_station"": { ""type"": ""number"" },
    ""station_interval"": { ""type"": ""number"" },
    ""min_elev"": { ""type"": ""number"" },
    ""max_elev"": { ""type"": ""number"" },
    ""elev_interval"": { ""type"": ""number"" },
    ""vertical_exaggeration"": { ""type"": ""number"" },
    ""ground_points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 2 } },
    ""grade_points"": { ""type"": ""array"", ""items"": { ""type"": ""array"", ""items"": { ""type"": ""number"" }, ""minItems"": 2, ""maxItems"": 2 } },
    ""road_layer"": { ""type"": ""string"" },
    ""cl_layer"": { ""type"": ""string"" },
    ""pavement_layer"": { ""type"": ""string"" },
    ""struct_layer"": { ""type"": ""string"" },
    ""grid_layer"": { ""type"": ""string"" },
    ""ground_layer"": { ""type"": ""string"" },
    ""grade_layer"": { ""type"": ""string"" },
    ""dim_layer"": { ""type"": ""string"" },
    ""note_layer"": { ""type"": ""string"" },
    ""citations"": { ""type"": ""array"", ""items"": {} },
    ""criteria_ref"": { ""type"": ""object"" },
    ""dry_run"": { ""type"": ""boolean"", ""default"": true },
    ""confirmation_token"": { ""type"": ""string"" }
  },
  ""additionalProperties"": false
}")
            });
        }
    }
}
