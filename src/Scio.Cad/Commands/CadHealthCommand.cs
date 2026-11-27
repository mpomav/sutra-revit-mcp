using System;
using System.Diagnostics;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Newtonsoft.Json.Linq;
using Scio.Contracts;
using Scio.Revit.Core;

namespace Scio.Cad.Commands
{
    internal static class CadHealthCommand
    {
        public static JObject Execute(JObject args, SutraCadApp app)
        {
            var docs = new JArray();
            Document active = Application.DocumentManager.MdiActiveDocument;
            string activeTitle = null;
            string activePath = null;

            foreach (Document d in Application.DocumentManager)
            {
                string path = d.Name;
                string title = Path.GetFileName(path);
                bool isActive = ReferenceEquals(d, active);
                if (isActive)
                {
                    activeTitle = title;
                    activePath = path;
                }
                docs.Add(new JObject
                {
                    ["title"] = title,
                    ["path"] = path,
                    ["is_active"] = isActive,
                    ["is_read_only"] = d.IsReadOnly
                });
            }

            bool c3d = Civil3DInspectCommand.IsCivil3DLoaded();

            return new JObject
            {
                ["status"] = "healthy",
                ["host"] = c3d ? "civil3d" : "autocad",
                ["cad_year"] = app.CadYear,
                ["autocad_version"] = Application.Version?.ToString(),
                ["civil3d_loaded"] = c3d,
                ["pid"] = Process.GetCurrentProcess().Id,
                ["addin_version"] = app.AddinVersion,
                ["contract_hash"] = Contract.Hash,
                ["protocol_version"] = Contract.ProtocolVersion,
                ["pipe_name"] = app.PipeName,
                ["active_document"] = activeTitle,
                ["active_document_path"] = activePath,
                ["open_document_count"] = docs.Count,
                ["open_documents"] = docs,
                ["data_paths"] = ScioPaths.Describe()
            };
        }
    }
}
