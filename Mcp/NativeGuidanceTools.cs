using System.ComponentModel;
using ModelContextProtocol.Server;

namespace SiemensMCPServer.Mcp
{
    [McpServerToolType]
    internal sealed class NativeGuidanceTools
    {
        private readonly NativeGuidance guidance;
        public NativeGuidanceTools(NativeGuidance guidance) { this.guidance = guidance; }
        [McpServerTool(Name = "siemens_list_workflows", ReadOnly = true, OpenWorld = false)]
        [Description("List current direct-native workflow/reference IDs, revision and hashes. No worker launch, TIA attachment or write approval.")]
        public object List() => guidance.Catalog();
        [McpServerTool(Name = "siemens_read_workflow", ReadOnly = true, OpenWorld = false)]
        [Description("Read one native workflow with the current catalog revision and returned exact SHA256.")]
        public object Workflow(string workflow_id, string expected_revision, string expected_sha256) => guidance.Get(workflow_id, expected_revision, expected_sha256, "workflow");
        [McpServerTool(Name = "siemens_read_reference", ReadOnly = true, OpenWorld = false)]
        [Description("Read a reference ID/hash pinned to the current direct-native catalog.")]
        public object Reference(string reference_id, string expected_revision, string expected_sha256) => guidance.Get(reference_id, expected_revision, expected_sha256, "reference");
        [McpServerTool(Name = "siemens_doctor", ReadOnly = true, OpenWorld = false)]
        [Description("Inspect direct-native identity and guide scope without native API probing or implicit worker/service startup.")]
        public object Doctor() => new { profile = "native-direct-candidate", version = AppRelease.Identity(typeof(NativeGuidanceTools).Assembly),
            lifetime = NativeHostLifetime.Identity, execution = NativeWorkAdmission.Snapshot(), manager = "Not reporting: no shared management endpoint", restart = "unavailable", guidance = guidance.Catalog(),
            nativeAcceptance = "separate coordinated disposable TIA pilot required; metadata is not native acceptance" };
    }
}
