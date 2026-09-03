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
        Assert.Contains("--name", File.ReadAllText(Path.Combine(_outputDirectory, "references", "commands.md")));
        Assert.Contains("\"greet\"", File.ReadAllText(Path.Combine(_outputDirectory, "references", "schemas.json")));

        File.AppendAllText(Path.Combine(_outputDirectory, "SKILL.md"), "stale");
        Assert.False(generator.IsCurrent(catalog, _outputDirectory));
    }

    [Fact]
    public void Generate_renders_examples_with_full_category_prefixed_command_path()
    {
        var catalog = OperationCatalog.Discover(typeof(CategorizedOperations));
        var generator = new SkillReferenceGenerator();
        var options = new SkillGenerationOptions("categorized-cli", "Categorized skill", "categorized-cli");

        generator.Generate(catalog, _outputDirectory, options);

        var skill = File.ReadAllText(Path.Combine(_outputDirectory, "SKILL.md"));
        var commands = File.ReadAllText(Path.Combine(_outputDirectory, "references", "commands.md"));

        Assert.Contains("`categorized-cli projects archived archive --id 42`", skill);
        Assert.Contains("`categorized-cli projects archived archive --id 42`", commands);
        Assert.DoesNotContain("`categorized-cli archive --id 42`", skill);
        Assert.DoesNotContain("`categorized-cli archive --id 42`", commands);
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

    private sealed class CategorizedOperations
    {
        [AgentOperation("archive", "Archives a project", Category = "projects archived", Examples = ["--id 42"])]
        public string Archive(int id) => $"Archived {id}";
    }
}
