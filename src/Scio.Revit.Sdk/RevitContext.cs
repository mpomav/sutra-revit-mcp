using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Scio.Abstractions;

namespace Scio.Revit.Sdk
{
    public static class IdCompat
    {
        public static long ToInt64(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId FromInt64(long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }

    public static class UnitKit
    {
        private const double MmPerFoot = 304.8;
        private const double SqMetersPerSqFoot = 0.09290304;
        private const double CuMetersPerCuFoot = 0.028316846592;

        public static double MmToFeet(double mm) => mm / MmPerFoot;
        public static double FeetToMm(double feet) => feet * MmPerFoot;
        public static double SqFeetToSqM(double sqFeet) => sqFeet * SqMetersPerSqFoot;
        public static double SqMToSqFeet(double sqM) => sqM / SqMetersPerSqFoot;
        public static double CuFeetToCuM(double cuFeet) => cuFeet * CuMetersPerCuFoot;
        public static double CuMToCuFeet(double cuM) => cuM / CuMetersPerCuFoot;
        public static double DegToRad(double deg) => deg * (Math.PI / 180.0);
        public static double RadToDeg(double rad) => rad * (180.0 / Math.PI);
    }

    public sealed class RevitContext
    {
        public UIApplication UiApplication { get; }
        public UIDocument? ActiveUiDocument => UiApplication.ActiveUIDocument;
        public Document? ActiveDocument => ActiveUiDocument?.Document;
        public PermissionProfile PermissionProfile { get; }
        public int RevitYear { get; }

        public RevitContext(UIApplication uiApp, PermissionProfile profile, int revitYear)
        {
            UiApplication = uiApp ?? throw new ArgumentNullException(nameof(uiApp));
            PermissionProfile = profile;
            RevitYear = revitYear;
        }

        public Document RequireActiveDocument(string? expectedTitleOrPath = null)
        {
            Document? doc = ActiveDocument;
            if (doc == null)
                throw new InvalidOperationException("No active Revit document is currently open.");

            if (!string.IsNullOrWhiteSpace(expectedTitleOrPath))
            {
                bool titleMatch = string.Equals(doc.Title, expectedTitleOrPath, StringComparison.OrdinalIgnoreCase);
                bool pathMatch = !string.IsNullOrWhiteSpace(doc.PathName) &&
                                 string.Equals(doc.PathName, expectedTitleOrPath, StringComparison.OrdinalIgnoreCase);
                if (!titleMatch && !pathMatch)
                {
                    throw new InvalidOperationException(
                        $"Active document mismatch: expected '{expectedTitleOrPath}', but active document is '{doc.Title}'.");
                }
            }

            return doc;
        }
    }

    public interface IRevitCommand
    {
        ToolDescriptor Descriptor { get; }
        ToolResponse Execute(RevitContext context, ToolRequest request);
    }
}
