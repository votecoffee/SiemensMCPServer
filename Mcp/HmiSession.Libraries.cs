using System.Linq;
using System.Text.Json;

namespace SiemensMCPServer.Mcp
{
    internal sealed partial class HmiSession : ILibraryInspectionBackend
    {
        public object ReadProjectExtension(string operation, string kind, JsonElement arguments)
            => new LibraryInspectionWorkflow(this, features).Read(operation, kind, arguments);

        public LibrarySnapshot CaptureLibraries(string expectedProjectPath, LibraryCapture capture)
        {
            CheckProject(); RequireExpectedPath(expectedProjectPath);
            using (var exclusive = portal!.ExclusiveAccess("Codex library metadata inspection"))
            {
                CheckProject(); RequireExpectedPath(expectedProjectPath);
                var snapshot = new LibraryCatalogReader().Capture(project!.ProjectLibrary, portal.GlobalLibraries.Cast<object>(), capture);
                CheckProject();
                return snapshot;
            }
        }
    }

    internal static class LibraryInspectionNative
    {
        public static object Capabilities() => new {
            projectLibraryApiPresent = typeof(Siemens.Engineering.Project).GetProperty("ProjectLibrary") != null,
            openGlobalLibrariesApiPresent = typeof(Siemens.Engineering.TiaPortal).GetProperty("GlobalLibraries") != null,
            nativeAcceptance = "pending", scope = "Read-only library metadata. No library open/upgrade/save, instance creation or online PLC services." };
    }
}
