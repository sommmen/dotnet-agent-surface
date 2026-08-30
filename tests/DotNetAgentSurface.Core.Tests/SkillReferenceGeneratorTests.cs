namespace DotNetAgentSurface.Core.Tests;

public sealed class SkillReferenceGeneratorTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(Path.GetTempPath(), $"dotnet-agent-surface-{Guid.NewGuid():N}");

    [Fact]
    public void Generate_creates_deterministic_reference_files_and_check_detects_changes()
    {
        var catalog = OperationCatalog.Discover(typeof(ReferenceOperations));
        var generator = new SkillReferenceGenerator();

        generator.Generate(catalog, _outputDirectory);

        Assert.True(generator.IsCurrent(catalog, _outputDirectory));
        Assert.Contains("`greet`", File.ReadAllText(Path.Combine(_outputDirectory, "SKILL.md")));
        Assert.Contains("--name", File.ReadAllText(Path.Combine(_outputDirectory, "commands.md")));
        Assert.Contains("\"greet\"", File.ReadAllText(Path.Combine(_outputDirectory, "schemas.json")));

        File.AppendAllText(Path.Combine(_outputDirectory, "SKILL.md"), "stale");
        Assert.False(generator.IsCurrent(catalog, _outputDirectory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }

    private sealed class ReferenceOperations
    {
        [AgentOperation("greet", "Greets a person")]
        public string Greet(string name) => $"Hello, {name}";
    }
}
