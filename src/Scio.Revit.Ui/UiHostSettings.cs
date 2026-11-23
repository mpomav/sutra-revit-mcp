using System.Collections.Generic;
using Scio.Abstractions;

namespace Scio.Revit.Ui
{
    /// <summary>
    /// Clean-room UI theme, host settings, and assistant models for Scio.Revit.Ui.
    /// </summary>
    public static class UiHostSettings
    {
        public static string UITheme { get; set; } = "auto";
        public static PermissionProfile PermissionProfile { get; set; } = PermissionProfile.SafeWrite;
        public static string DataRoot => ScioRuntimePaths.DataRoot;
    }

    public sealed class CatalogEntry
    {
        public string Tool { get; set; } = string.Empty;
        public string PromptId { get; set; } = string.Empty;
        public string TitleEs { get; set; } = string.Empty;
        public string TitleEn { get; set; } = string.Empty;
        public ToolDescriptor? Descriptor { get; set; }
        public bool Allowed { get; set; } = true;
        public string Title(bool es) => es ? (string.IsNullOrEmpty(TitleEs) ? Tool : TitleEs) : (string.IsNullOrEmpty(TitleEn) ? Tool : TitleEn);
    }

    public sealed class AssistantContext
    {
        public bool HasDocument { get; set; }
        public string DocumentTitle { get; set; } = string.Empty;
        public string ActiveViewName { get; set; } = string.Empty;
        public List<long> SelectedIds { get; set; } = new List<long>();
        public Dictionary<string, int> Categories { get; set; } = new Dictionary<string, int>();
        public bool HasSelection => SelectedIds.Count > 0;
    }
}
