namespace SiemensMCPServer
{
    internal interface ITextListDocument
    {
        string Name { get; }
        string Fingerprint { get; }
        string ArtifactExtension { get; }
        string NativeImportScope { get; }
        TextValue[] Read();
        ITextListDocument Edit(TextEdit[] edits);
        string Serialize();
    }
}
