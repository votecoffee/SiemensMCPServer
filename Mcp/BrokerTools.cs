using System;
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SiemensMCPServer.Mcp
{
    [McpServerToolType]
    internal sealed class BrokerTools
    {
        private readonly BrokerPublication publication = new BrokerPublication();

        [McpServerTool(Name = "tia_list_broker_capabilities", ReadOnly = true, OpenWorld = false)]
        [Description("Publish the normalized Universal Tools capability catalog for Siemens workflow guides. Installation metadata only: no native API loading, project attachment, extension startup, confirmation or writes. Catalog entries are discovery hints, not executable operations or authorization.")]
        public CallToolResult Catalog() => Result(() => publication.Catalog());

        [McpServerTool(Name = "tia_read_workflow", ReadOnly = true, OpenWorld = false)]
        [Description("Read a published Siemens workflow using its exact ID/revision from tia_list_broker_capabilities. Returns bounded Markdown, native tool references and hash-pinned guide readers. Rejects stale revisions; no filesystem paths or native project access.")]
        public CallToolResult Read(string workflowId, string expectedRevision) => Result(() => publication.Read(workflowId, expectedRevision));

        [McpServerTool(Name = "tia_read_workflow_reference", ReadOnly = true, OpenWorld = false)]
        [Description("Read one embedded public guide using the workflow ID/revision, reference ID and SHA-256 returned by tia_read_workflow. Arbitrary files, executable assets, native state and customer evidence are excluded.")]
        public CallToolResult Reference(string workflowId, string expectedRevision, string referenceId, string expectedSha256)
            => Result(() => publication.ReadReference(workflowId, expectedRevision, referenceId, expectedSha256));

        private static CallToolResult Result(Func<object> read)
        {
            try {
                object value = read();
                return new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(value), Content = new[] { new TextContentBlock { Text = JsonSerializer.Serialize(value) } } };
            }
            catch (Exception ex) { return new CallToolResult { IsError = true, Content = new[] { new TextContentBlock { Text = ex.GetBaseException().Message } } }; }
        }
    }
}
