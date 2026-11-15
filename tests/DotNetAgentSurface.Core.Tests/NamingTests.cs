namespace DotNetAgentSurface.Core.Tests;

public sealed class OperationNamingTests
{
    [Theory]
    [InlineData("contextName", "context-name")]
    [InlineData("ContextName", "context-name")]
    [InlineData("context_name", "context-name")]
    [InlineData("run_query", "run-query")]
    [InlineData("run-query", "run-query")]
    [InlineData("includeAllBuilds", "include-all-builds")]
    [InlineData("parseURLValue", "parse-url-value")]
    [InlineData("id", "id")]
    public void ToKebabCase_converts_identifiers(string name, string expected) =>
        Assert.Equal(expected, OperationNaming.ToKebabCase(name));

    [Theory]
    [InlineData("--context-name")]
    [InlineData("contextName")]
    [InlineData("context_name")]
    public void ToMatchKey_ignores_case_and_separators(string name) =>
        Assert.Equal("contextname", OperationNaming.ToMatchKey(name.TrimStart('-')));
}
