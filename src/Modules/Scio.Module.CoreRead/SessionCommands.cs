using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Scio.Abstractions;
using Scio.Revit.Sdk;

namespace Scio.Module.CoreRead
{
    [ScioTool("get_document_info", "core", "Read metadata, project info, levels, phases, worksharing state, and category summary of the active Revit document.", ReadOnly = true)]
    public sealed class GetDocumentInfoCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(GetDocumentInfoCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var pi = doc.ProjectInformation;

            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .Select(l => new JObject
                {
                    ["id"] = IdCompat.ToInt64(l.Id),
                    ["name"] = l.Name,
                    ["elevation_mm"] = Math.Round(UnitKit.FeetToMm(l.Elevation), 1)
                });

            var phases = new JArray();
            foreach (Phase ph in doc.Phases)
                phases.Add(new JObject { ["id"] = IdCompat.ToInt64(ph.Id), ["name"] = ph.Name });

            int linkCount = new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).GetElementCount();
            int warningCount = doc.GetWarnings().Count;
            var activeView = context.ActiveUiDocument?.ActiveView;

            var data = new JObject
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["is_workshared"] = doc.IsWorkshared,
                ["is_family_document"] = doc.IsFamilyDocument,
                ["is_modified"] = doc.IsModified,
                ["revit_year"] = context.RevitYear,
                ["active_view"] = activeView == null ? null : new JObject
                {
                    ["id"] = IdCompat.ToInt64(activeView.Id),
                    ["name"] = activeView.Name,
                    ["view_type"] = activeView.ViewType.ToString()
                },
                ["project_info"] = pi == null ? null : new JObject
                {
                    ["name"] = pi.Name ?? string.Empty,
                    ["number"] = pi.Number ?? string.Empty,
                    ["client_name"] = pi.ClientName ?? string.Empty,
                    ["building_name"] = pi.BuildingName ?? string.Empty,
                    ["address"] = pi.Address ?? string.Empty,
                    ["status"] = pi.Status ?? string.Empty,
                    ["issue_date"] = pi.IssueDate ?? string.Empty
                },
                ["levels"] = new JArray(levels),
                ["phases"] = phases,
                ["link_count"] = linkCount,
                ["warning_count"] = warningCount
            };

            return ToolResponse.Success(request.RequestId, data, $"Document '{doc.Title}' ({doc.Phases.Size} phases, {warningCount} warnings).");
        }
    }

    [ScioTool("scio_open_document", "core", "Open a Revit .rvt or .rfa file either in the active UI or in background mode.", ReadOnly = false)]
    public sealed class OpenDocumentCommand : IRevitCommand
    {
        [ScioParam("path", "Absolute path to the .rvt or .rfa file to open.", Required = true)]
        public string PathParam { get; set; } = string.Empty;

        [ScioParam("activate", "If true (default), opens and activates the document in Revit UI. If false, opens in background.")]
        public bool Activate { get; set; } = true;

        [ScioParam("detach", "If true, detaches from central preserving worksets (for workshared models).")]
        public bool Detach { get; set; }

        [ScioParam("audit", "If true, runs Revit audit on open.")]
        public bool Audit { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(OpenDocumentCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string filePath = request.Arguments.Value<string>("path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return ToolResponse.Refusal(request.RequestId, "file_not_found", $"File not found: '{filePath}'.");

            bool activate = request.Arguments.Value<bool?>("activate") ?? true;
            bool detach = request.Arguments.Value<bool?>("detach") ?? false;
            bool audit = request.Arguments.Value<bool?>("audit") ?? false;

            var modelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(filePath);
            var opts = new OpenOptions
            {
                Audit = audit,
                DetachFromCentralOption = detach
                    ? DetachFromCentralOption.DetachAndPreserveWorksets
                    : DetachFromCentralOption.DoNotDetach
            };

            Document opened;
            if (activate)
            {
                var uiDoc = context.UiApplication.OpenAndActivateDocument(modelPath, opts, false);
                opened = uiDoc.Document;
            }
            else
            {
                opened = context.UiApplication.Application.OpenDocumentFile(modelPath, opts);
            }

            var data = new JObject
            {
                ["title"] = opened.Title,
                ["path"] = opened.PathName,
                ["activated"] = activate,
                ["is_workshared"] = opened.IsWorkshared,
                ["is_family_document"] = opened.IsFamilyDocument
            };
            return ToolResponse.Success(request.RequestId, data, $"Opened '{opened.Title}'.");
        }
    }

    [ScioTool("scio_save_document", "core", "Save the active Revit document, or SaveAs to a new path.", ReadOnly = false)]
    public sealed class SaveDocumentCommand : IRevitCommand
    {
        [ScioParam("save_as_path", "Optional new absolute .rvt/.rfa path for SaveAs.")]
        public string SaveAsPath { get; set; } = string.Empty;

        [ScioParam("overwrite", "Allow overwriting an existing file when using save_as_path.")]
        public bool Overwrite { get; set; }

        [ScioParam("compact", "Compact file on SaveAs.")]
        public bool Compact { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(SaveDocumentCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            string? saveAs = request.Arguments.Value<string>("save_as_path");
            bool overwrite = request.Arguments.Value<bool?>("overwrite") ?? false;
            bool compact = request.Arguments.Value<bool?>("compact") ?? false;

            if (!string.IsNullOrWhiteSpace(saveAs))
            {
                var opts = new SaveAsOptions
                {
                    OverwriteExistingFile = overwrite,
                    Compact = compact
                };
                doc.SaveAs(saveAs, opts);
                return ToolResponse.Success(request.RequestId, new JObject
                {
                    ["title"] = doc.Title,
                    ["path"] = doc.PathName,
                    ["mode"] = "save_as"
                }, $"Saved document as '{doc.PathName}'.");
            }

            if (string.IsNullOrWhiteSpace(doc.PathName))
                return ToolResponse.Refusal(request.RequestId, "unsaved_document", "Document has no path yet. Provide 'save_as_path'.");

            doc.Save();
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["title"] = doc.Title,
                ["path"] = doc.PathName,
                ["mode"] = "save"
            }, $"Saved '{doc.Title}'.");
        }
    }

    [ScioTool("scio_relinquish_all", "core", "Relinquish all checked-out worksets and borrowed elements in a workshared Revit document.", ReadOnly = false)]
    public sealed class RelinquishAllCommand : IRevitCommand
    {
        [ScioParam("comment", "Optional comment logged with the relinquish operation.")]
        public string Comment { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(RelinquishAllCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            if (!doc.IsWorkshared)
                return ToolResponse.Refusal(request.RequestId, "not_workshared", $"Document '{doc.Title}' is not workshared.");

            var relOpts = new RelinquishOptions(true);
            var transOpts = new TransactWithCentralOptions();
            var result = WorksharingUtils.RelinquishOwnership(doc, relOpts, transOpts);

            var data = new JObject
            {
                ["title"] = doc.Title,
                ["relinquished_elements"] = result.GetRelinquishedElements().Count,
                ["relinquished_worksets"] = result.GetRelinquishedWorksets().Count
            };
            return ToolResponse.Success(request.RequestId, data, $"Relinquished ownership in '{doc.Title}'.");
        }
    }

    [ScioTool("scio_document_session", "core", "List all open Revit documents, or close a non-active document cleanly.", ReadOnly = false)]
    public sealed class DocumentSessionCommand : IRevitCommand
    {
        [ScioParam("action", "Operation: list (default) or close.", EnumValues = new[] { "list", "close" })]
        public string Action { get; set; } = "list";

        [ScioParam("title", "Document title to close when action=close.")]
        public string Title { get; set; } = string.Empty;

        [ScioParam("save_modified", "Whether to save before closing (default false).")]
        public bool SaveModified { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(DocumentSessionCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string action = (request.Arguments.Value<string>("action") ?? "list").Trim().ToLowerInvariant();
            var docs = context.UiApplication.Application.Documents;
            var activeDoc = context.ActiveDocument;

            if (action == "close")
            {
                string title = request.Arguments.Value<string>("title") ?? string.Empty;
                bool saveMod = request.Arguments.Value<bool?>("save_modified") ?? false;
                if (string.IsNullOrWhiteSpace(title))
                    return ToolResponse.Refusal(request.RequestId, "missing_title", "Provide 'title' to close a document.");

                foreach (Document d in docs)
                {
                    if (d.IsLinked) continue;
                    if (!string.Equals(d.Title, title, StringComparison.OrdinalIgnoreCase)) continue;
                    if (activeDoc != null && string.Equals(activeDoc.Title, d.Title, StringComparison.OrdinalIgnoreCase))
                        return ToolResponse.Refusal(request.RequestId, "cannot_close_active", "Revit API does not allow closing the active UI document directly.");

                    bool closed = d.Close(saveMod);
                    return ToolResponse.Success(request.RequestId, new JObject
                    {
                        ["title"] = title,
                        ["closed"] = closed,
                        ["saved"] = saveMod
                    }, closed ? $"Closed '{title}'." : $"Revit declined to close '{title}'.");
                }
                return ToolResponse.Refusal(request.RequestId, "document_not_found", $"Open document '{title}' not found.");
            }

            var list = new JArray();
            foreach (Document d in docs)
            {
                list.Add(new JObject
                {
                    ["title"] = d.Title,
                    ["path"] = d.PathName,
                    ["is_active"] = activeDoc != null && ReferenceEquals(d, activeDoc),
                    ["is_linked"] = d.IsLinked,
                    ["is_workshared"] = d.IsWorkshared,
                    ["is_family"] = d.IsFamilyDocument,
                    ["is_modified"] = d.IsModified
                });
            }

            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["count"] = list.Count,
                ["documents"] = list
            }, $"Found {list.Count} open document(s).");
        }
    }

    [ScioTool("scio_file_info", "core", "Inspect BasicFileInfo of a Revit .rvt/.rfa file on disk without opening it.", ReadOnly = true)]
    public sealed class FileInfoCommand : IRevitCommand
    {
        [ScioParam("path", "Absolute path to the .rvt or .rfa file.", Required = true)]
        public string FilePath { get; set; } = string.Empty;

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(FileInfoCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            string filePath = request.Arguments.Value<string>("path") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return ToolResponse.Refusal(request.RequestId, "file_not_found", $"File not found: '{filePath}'.");

            var info = BasicFileInfo.Extract(filePath);
            var fi = new FileInfo(filePath);
            var data = new JObject
            {
                ["path"] = filePath,
                ["size_bytes"] = fi.Length,
                ["last_write_utc"] = fi.LastWriteTimeUtc.ToString("o"),
                ["format"] = info.Format,
                ["is_workshared"] = info.IsWorkshared,
                ["is_central"] = info.IsCentral,
                ["is_local"] = info.IsLocal,
                ["central_path"] = info.CentralPath ?? string.Empty,
                ["username"] = info.Username ?? string.Empty,
                ["language"] = info.LanguageWhenSaved.ToString(),
                ["is_saved_in_current_version"] = info.IsSavedInCurrentVersion,
                ["is_saved_in_later_version"] = info.IsSavedInLaterVersion
            };
            return ToolResponse.Success(request.RequestId, data, $"Revit {info.Format} file ({fi.Length / 1024} KB).");
        }
    }

    [ScioTool("scio_undo", "core", "Post Revit's built-in Undo command to revert the last committed transaction.", ReadOnly = false)]
    public sealed class UndoCommand : IRevitCommand
    {
        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(UndoCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            RevitCommandId cmdId = RevitCommandId.LookupPostableCommandId(PostableCommand.Undo);
            if (!context.UiApplication.CanPostCommand(cmdId))
                return ToolResponse.Refusal(request.RequestId, "undo_unavailable", "Revit Undo command is not currently available.");

            context.UiApplication.PostCommand(cmdId);
            return ToolResponse.Success(request.RequestId, new JObject
            {
                ["document"] = doc.Title,
                ["posted"] = true
            }, $"Posted Undo to '{doc.Title}'.", VerificationStatus.Unverified, hostVerified: false);
        }
    }

    [ScioTool("scio_verify_changes", "core", "Re-read specific element IDs to verify existence, deletion, or expected parameter values.", ReadOnly = true)]
    public sealed class VerifyChangesCommand : IRevitCommand
    {
        [ScioParam("element_ids", "Array of element IDs to verify.", Required = true)]
        public List<long> ElementIds { get; set; } = new List<long>();

        [ScioParam("expect_deleted", "If true, asserts that the listed IDs no longer exist in the document.")]
        public bool ExpectDeleted { get; set; }

        [ScioParam("expected_params", "Optional map of parameter name -> expected display value.")]
        public JObject? ExpectedParams { get; set; }

        public ToolDescriptor Descriptor { get; } = JsonSchemaBuilder.DescribeTool(typeof(VerifyChangesCommand));

        public ToolResponse Execute(RevitContext context, ToolRequest request)
        {
            var doc = context.RequireActiveDocument(request.TargetDocument);
            var ids = request.Arguments["element_ids"] as JArray;
            if (ids == null || ids.Count == 0)
                return ToolResponse.Refusal(request.RequestId, "missing_ids", "Provide non-empty 'element_ids'.");

            bool expectDeleted = request.Arguments.Value<bool?>("expect_deleted") ?? false;
            var expectedParams = request.Arguments["expected_params"] as JObject;

            int passed = 0;
            int failed = 0;
            var checks = new JArray();

            foreach (var tok in ids)
            {
                var el = RevitElementKit.ResolveElement(doc, tok);
                bool exists = el != null;
                bool ok = expectDeleted ? !exists : exists;
                var mismatches = new JArray();

                if (!expectDeleted && el != null && expectedParams != null)
                {
                    foreach (var prop in expectedParams.Properties())
                    {
                        var p = RevitElementKit.FindParameter(el, prop.Name, includeType: true);
                        string actual = RevitElementKit.ReadParameterDisplay(p);
                        string expected = prop.Value?.ToString() ?? string.Empty;
                        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        {
                            ok = false;
                            mismatches.Add(new JObject
                            {
                                ["parameter"] = prop.Name,
                                ["expected"] = expected,
                                ["actual"] = actual
                            });
                        }
                    }
                }

                if (ok) passed++; else failed++;
                checks.Add(new JObject
                {
                    ["id"] = tok.ToString(),
                    ["exists"] = exists,
                    ["ok"] = ok,
                    ["mismatches"] = mismatches
                });
            }

            var data = new JObject
            {
                ["passed"] = passed,
                ["failed"] = failed,
                ["all_verified"] = failed == 0,
                ["checks"] = checks
            };

            return ToolResponse.Success(
                request.RequestId,
                data,
                $"Verified {passed}/{passed + failed} element(s).",
                failed == 0 ? VerificationStatus.Verified : VerificationStatus.Partial,
                hostVerified: true);
        }
    }
}
