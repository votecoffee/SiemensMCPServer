using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SiemensMCPServer.Analysis
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiResourceInput
    {
        public string HmiId { get; set; } = "";
        [JsonRequired] public string Path { get; set; } = "";
        [JsonRequired] public string Xml { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiConnectionMapping
    {
        public string HmiId { get; set; } = "";
        [JsonRequired] public string Connection { get; set; } = "";
        [JsonRequired] public string ControllerId { get; set; } = "";
        public string DefaultScope { get; set; } = "";
        public string AddressSyntax { get; set; } = "canonical";
        public string ExpectedControllerName { get; set; } = "";
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiAnalysisInput
    {
        [JsonRequired] public HmiResourceInput[] Resources { get; set; } = Array.Empty<HmiResourceInput>();
        public ControllerDocumentInput[] ControllerDocuments { get; set; } = Array.Empty<ControllerDocumentInput>();
        public HmiConnectionMapping[] ConnectionMappings { get; set; } = Array.Empty<HmiConnectionMapping>();
        public HmiConnectionDefinition[] ConnectionDefinitions { get; set; } = Array.Empty<HmiConnectionDefinition>();
        public bool ConnectionMetadataAvailable { get; set; }
        public bool ExportReportedComplete { get; set; }
        public string[] ExportFailures { get; set; } = Array.Empty<string>();
        public string[] UnsupportedResources { get; set; } = Array.Empty<string>();
    }
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    internal sealed class HmiConnectionDefinition
    {
        public string HmiId { get; set; } = "";
        [JsonRequired] public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string NativeType { get; set; } = "";
        public Dictionary<string, string> Attributes { get; set; } = new Dictionary<string, string>();
    }
    internal sealed class AnalysisDiagnostic
    {
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string HmiId { get; set; } = "";
        public string ResourcePath { get; set; } = "";
        public string XPath { get; set; } = "";
    }
    internal sealed class HmiTagDeclaration
    {
        public string Id { get; set; } = "";
        public string HmiId { get; set; } = "";
        public string ResourcePath { get; set; } = "";
        public string XPath { get; set; } = "";
        public string Name { get; set; } = "";
        public string DataType { get; set; } = "";
        public string HmiDataType { get; set; } = "";
        public DeclarationDimension[] Dimensions { get; set; } = Array.Empty<DeclarationDimension>();
        public string Connection { get; set; } = "";
        public string ControllerSymbol { get; set; } = "";
        public string AddressMode { get; set; } = "";
        public string LogicalAddress { get; set; } = "";
        public string NativeLength { get; set; } = "";
        public bool Multiplexed { get; set; }
        public string AliasTag { get; set; } = "";
        public string[] Issues { get; set; } = Array.Empty<string>();
    }
    internal sealed class HmiBindingValidation
    {
        public string TagId { get; set; } = "";
        public string HmiId { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Connection { get; set; } = "";
        public string ControllerId { get; set; } = "";
        public string ControllerAddress { get; set; } = "";
        public string Status { get; set; } = "unresolved";
        public string[] Issues { get; set; } = Array.Empty<string>();
        public string[] Warnings { get; set; } = Array.Empty<string>();
        public SymbolResolution? Resolution { get; set; }
        public bool NativePartnerVerified => false;
    }
    internal sealed class HmiReferenceOccurrence
    {
        public string Id { get; set; } = "";
        public string HmiId { get; set; } = "";
        public string ResourcePath { get; set; } = "";
        public string ResourceName { get; set; } = "";
        public string ResourceType { get; set; } = "";
        public string XPath { get; set; } = "";
        public string OwnerXPath { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public string OwnerType { get; set; } = "";
        public string ControlXPath { get; set; } = "";
        public string ControlName { get; set; } = "";
        public string[] Context { get; set; } = Array.Empty<string>();
        public string LinkType { get; set; } = "";
        public string ReferenceText { get; set; } = "";
        public string TagId { get; set; } = "";
        public string Connection { get; set; } = "";
        public string ControllerId { get; set; } = "";
        public string ControllerAddress { get; set; } = "";
        public string Status { get; set; } = "unresolved";
        public string[] Issues { get; set; } = Array.Empty<string>();
        public string[] Chain { get; set; } = Array.Empty<string>();
        public SymbolResolution? Resolution { get; set; }
    }
    internal sealed class HmiAnalysisReport
    {
        public string Format => "tia-hmi-reference-analysis-v1";
        public string EngineVersion => typeof(HmiReferenceAnalyzer).Assembly.GetName().Version!.ToString(3);
        public string AnalysisSha256 { get; set; } = "";
        public object[] Sources { get; set; } = Array.Empty<object>();
        public ControllerDeclaration[] Controllers { get; set; } = Array.Empty<ControllerDeclaration>();
        public HmiTagDeclaration[] Tags { get; set; } = Array.Empty<HmiTagDeclaration>();
        public HmiBindingValidation[] Bindings { get; set; } = Array.Empty<HmiBindingValidation>();
        public HmiReferenceOccurrence[] References { get; set; } = Array.Empty<HmiReferenceOccurrence>();
        public AnalysisDiagnostic[] Diagnostics { get; set; } = Array.Empty<AnalysisDiagnostic>();
        public bool ExportReportedComplete { get; set; }
        public string[] ExportFailures { get; set; } = Array.Empty<string>();
        public string[] UnsupportedResources { get; set; } = Array.Empty<string>();
        public HmiConnectionDefinition[] ConnectionDefinitions { get; set; } = Array.Empty<HmiConnectionDefinition>();
        public bool ConnectionMetadataAvailable { get; set; }
        public bool NativeBindingVerified => false;
        public string Scope => "Read-only classic HMI export/declaration analysis. Static exported links only; scripts, computed/indirect references, unexported objects, native partners, runtime and physical offsets require separate checks.";
    }
}
