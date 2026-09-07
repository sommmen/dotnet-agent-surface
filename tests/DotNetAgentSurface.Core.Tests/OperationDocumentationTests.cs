using System.Reflection;

namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationDocumentationTests
{
    [Fact]
    public void Discover_uses_xml_summary_and_parameter_documentation()
    {
        var source = new StubDocumentationSource(new OperationDocumentation(
            "Fetches a widget.",
            null,
            new Dictionary<string, string> { ["id"] = "The widget identifier." },
            null));

        var operation = Assert.Single(OperationCatalog.Discover(source, typeof(UndocumentedOperations)).Operations);

        Assert.Equal("Fetches a widget.", operation.Description);
        Assert.Equal("The widget identifier.", Assert.Single(operation.Parameters).Description);
    }

    [Fact]
    public void Discover_prefers_explicit_description_over_documentation()
    {
        var source = new StubDocumentationSource(new OperationDocumentation("Inferred.", null, new Dictionary<string, string>(), null));

        var operation = Assert.Single(OperationCatalog.Discover(source, typeof(ExplicitOperations)).Operations);

        Assert.Equal("Explicit.", operation.Description);
    }

    [Fact]
    public void Discover_allows_name_only_operations_without_documentation_unless_strict()
    {
        var catalog = OperationCatalog.Discover(typeof(UndocumentedOperations));
        Assert.Equal(string.Empty, Assert.Single(catalog.Operations).Description);
        Assert.Single(catalog.DocumentationDiagnostics);

        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(null, new OperationDocumentationOptions { RequireDescription = true }, typeof(UndocumentedOperations)));
        Assert.Contains("empty description", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discover_rejects_explicit_blank_description()
    {
        var exception = Assert.Throws<OperationCatalogException>(() => OperationCatalog.Discover(typeof(BlankOperations)));
        Assert.Contains("empty description", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Xml_source_matches_compiler_method_id_and_normalizes_text()
    {
        var method = typeof(UndocumentedOperations).GetMethod(nameof(UndocumentedOperations.Fetch))!;
        var id = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.UndocumentedOperations.Fetch(System.String)";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $"<doc><members><member name=\"{id}\"><summary> Fetches\n <paramref name=\"id\"/>. </summary><param name=\"id\"> The  id. </param></member></members></doc>");
            var documentation = new XmlOperationDocumentationSource(path).GetDocumentation(method)!;
            Assert.Equal("Fetches id.", documentation.Summary);
            Assert.Equal("The id.", documentation.Parameters["id"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xml_source_falls_back_silently_when_the_file_is_missing()
    {
        var method = typeof(UndocumentedOperations).GetMethod(nameof(UndocumentedOperations.Fetch))!;
        var missingPath = Path.Combine(Path.GetTempPath(), $"dotnet-agent-surface-missing-{Guid.NewGuid():N}.xml");

        var documentation = new XmlOperationDocumentationSource(missingPath).GetDocumentation(method);

        Assert.Null(documentation);
    }

    [Fact]
    public void Xml_source_falls_back_silently_when_the_file_is_malformed()
    {
        var method = typeof(UndocumentedOperations).GetMethod(nameof(UndocumentedOperations.Fetch))!;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "<doc><members><member name=\"not closed properly");

            var documentation = new XmlOperationDocumentationSource(path).GetDocumentation(method);

            Assert.Null(documentation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(nameof(DocumentationCommentIdOperations.WithArrayParameter), "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.DocumentationCommentIdOperations.WithArrayParameter(System.String[])")]
    [InlineData(nameof(DocumentationCommentIdOperations.WithRefParameter), "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.DocumentationCommentIdOperations.WithRefParameter(System.Int32@)")]
    [InlineData(nameof(DocumentationCommentIdOperations.WithOutParameter), "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.DocumentationCommentIdOperations.WithOutParameter(System.Int32@)")]
    [InlineData(nameof(DocumentationCommentIdOperations.WithInParameter), "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.DocumentationCommentIdOperations.WithInParameter(System.Int32@)")]
    [InlineData(nameof(DocumentationCommentIdOperations.WithGenericParameter), "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.DocumentationCommentIdOperations.WithGenericParameter``1(``0)")]
    public void Xml_source_generates_compiler_matching_ids_for_arrays_generics_and_ref_like_parameters(string methodName, string id)
    {
        var method = typeof(DocumentationCommentIdOperations).GetMethod(methodName)!;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $"<doc><members><member name=\"{id}\"><summary>Matched.</summary></member></members></doc>");
            var documentation = new XmlOperationDocumentationSource(path).GetDocumentation(method);
            Assert.Equal("Matched.", documentation?.Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xml_source_resolves_trivial_inheritdoc_from_overridden_base_member()
    {
        var overrideMethod = typeof(InheritdocDerivedOperations).GetMethod(nameof(InheritdocDerivedOperations.Fetch))!;
        var baseId = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.InheritdocBaseOperations.Fetch(System.String)";
        var overrideId = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.InheritdocDerivedOperations.Fetch(System.String)";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                $"<doc><members>" +
                $"<member name=\"{baseId}\"><summary> Fetches an item. </summary><param name=\"id\"> The id. </param></member>" +
                $"<member name=\"{overrideId}\"><inheritdoc/></member>" +
                "</members></doc>");

            var documentation = new XmlOperationDocumentationSource(path).GetDocumentation(overrideMethod)!;

            Assert.Equal("Fetches an item.", documentation.Summary);
            Assert.Equal("The id.", documentation.Parameters["id"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xml_source_resolves_trivial_inheritdoc_from_single_interface_member()
    {
        var implementationMethod = typeof(InheritdocImplementingOperations).GetMethod(nameof(InheritdocImplementingOperations.Fetch))!;
        var interfaceId = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.IInheritdocOperations.Fetch(System.String)";
        var implementationId = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.InheritdocImplementingOperations.Fetch(System.String)";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                $"<doc><members>" +
                $"<member name=\"{interfaceId}\"><summary> Fetches from the interface. </summary></member>" +
                $"<member name=\"{implementationId}\"><inheritdoc/></member>" +
                "</members></doc>");

            var documentation = new XmlOperationDocumentationSource(path).GetDocumentation(implementationMethod)!;

            Assert.Equal("Fetches from the interface.", documentation.Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Xml_source_treats_ambiguous_inheritdoc_as_no_documentation()
    {
        var implementationMethod = typeof(AmbiguousInheritdocOperations).GetMethod(nameof(AmbiguousInheritdocOperations.Fetch))!;
        var firstInterfaceId = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.IFirstInheritdocOperations.Fetch(System.String)";
        var secondInterfaceId = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.ISecondInheritdocOperations.Fetch(System.String)";
        var implementationId = "M:DotNetAgentSurface.Core.Tests.OperationDocumentationTests.AmbiguousInheritdocOperations.Fetch(System.String)";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                $"<doc><members>" +
                $"<member name=\"{firstInterfaceId}\"><summary> From the first interface. </summary></member>" +
                $"<member name=\"{secondInterfaceId}\"><summary> From the second interface. </summary></member>" +
                $"<member name=\"{implementationId}\"><inheritdoc/></member>" +
                "</members></doc>");

            var documentation = new XmlOperationDocumentationSource(path).GetDocumentation(implementationMethod);

            Assert.Null(documentation);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class StubDocumentationSource(OperationDocumentation documentation) : IOperationDocumentationSource
    {
        public OperationDocumentation? GetDocumentation(MethodInfo method) => documentation;
    }

    private sealed class UndocumentedOperations
    {
        [AgentOperation("fetch")]
        public void Fetch(string id) { }
    }

    private sealed class DocumentationCommentIdOperations
    {
        public void WithArrayParameter(string[] values) { }

        public void WithRefParameter(ref int value) { }

        public void WithOutParameter(out int value) => value = 0;

        public void WithInParameter(in int value) { }

        public void WithGenericParameter<T>(T value) { }
    }

    private abstract class InheritdocBaseOperations
    {
        public abstract void Fetch(string id);
    }

    private sealed class InheritdocDerivedOperations : InheritdocBaseOperations
    {
        public override void Fetch(string id) { }
    }

    private interface IInheritdocOperations
    {
        void Fetch(string id);
    }

    private sealed class InheritdocImplementingOperations : IInheritdocOperations
    {
        public void Fetch(string id) { }
    }

    private interface IFirstInheritdocOperations
    {
        void Fetch(string id);
    }

    private interface ISecondInheritdocOperations
    {
        void Fetch(string id);
    }

    private sealed class AmbiguousInheritdocOperations : IFirstInheritdocOperations, ISecondInheritdocOperations
    {
        public void Fetch(string id) { }
    }

    private sealed class ExplicitOperations
    {
        [AgentOperation("explicit", "Explicit.")]
        public void Run() { }
    }

    private sealed class BlankOperations
    {
        [AgentOperation("blank", " ")]
        public void Run() { }
    }
}
