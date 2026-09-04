using System.Text;

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

    [Fact]
    public void Generate_keeps_SKILL_md_within_the_documented_size_budget_for_large_catalogs()
    {
        var catalog = BuildSyntheticCatalog(categoryCount: 6, operationsPerCategory: 10);
        var generator = new SkillReferenceGenerator();
        var options = new SkillGenerationOptions("large-cli", "A skill with a large, sharded catalog", "large-cli");

        generator.Generate(catalog, _outputDirectory, options);

        var skill = File.ReadAllText(Path.Combine(_outputDirectory, "SKILL.md"));
        var lineCount = skill.Split('\n').Length;
        var byteCount = Encoding.UTF8.GetByteCount(skill);

        Assert.True(lineCount < 150, $"Expected SKILL.md to stay under 150 lines but it had {lineCount}.");
        Assert.True(byteCount < 4096, $"Expected SKILL.md to stay under ~4KB but it was {byteCount} bytes.");

        // The detail lives under references/ instead of being inlined into SKILL.md.
        Assert.True(Directory.EnumerateFiles(Path.Combine(_outputDirectory, "references", "commands")).Any());
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void Generate_shards_commands_only_once_the_operation_count_threshold_is_exceeded(int operationCount, bool expectSharded)
    {
        var catalog = BuildSyntheticCatalog(categoryCount: 1, operationsPerCategory: operationCount);
        var generator = new SkillReferenceGenerator();
        var options = new SkillGenerationOptions("threshold-cli", "A skill used to probe the sharding threshold", "threshold-cli", categoryShardThreshold: 20);

        generator.Generate(catalog, _outputDirectory, options);

        var commandDirectory = Path.Combine(_outputDirectory, "references", "commands");
        var shardFiles = Directory.Exists(commandDirectory) ? Directory.EnumerateFiles(commandDirectory).ToArray() : [];
        var commands = File.ReadAllText(Path.Combine(_outputDirectory, "references", "commands.md"));

        if (expectSharded)
        {
            Assert.NotEmpty(shardFiles);
            Assert.DoesNotContain("## `category0-op0`", commands);
        }
        else
        {
            Assert.Empty(shardFiles);
            Assert.Contains("## `category0-op0`", commands);
        }
    }

    [Fact]
    public void Generate_removes_orphaned_category_files_and_check_reports_them_as_stale()
    {
        var catalog = BuildSyntheticCatalog(categoryCount: 3, operationsPerCategory: 10);
        var generator = new SkillReferenceGenerator();
        var options = new SkillGenerationOptions("orphan-cli", "A skill used to probe orphan detection", "orphan-cli");

        generator.Generate(catalog, _outputDirectory, options);
        Assert.True(generator.IsCurrent(catalog, _outputDirectory, options));

        var commandDirectory = Path.Combine(_outputDirectory, "references", "commands");
        var orphanFile = Path.Combine(commandDirectory, "removed-category.md");
        File.WriteAllText(orphanFile, "# Stale category" + Environment.NewLine);

        Assert.False(generator.IsCurrent(catalog, _outputDirectory, options));

        generator.Generate(catalog, _outputDirectory, options);

        Assert.False(File.Exists(orphanFile));
        Assert.True(generator.IsCurrent(catalog, _outputDirectory, options));
    }

    [Fact]
    public void Generate_writes_valid_YAML_frontmatter_with_non_empty_name_and_description()
    {
        var catalog = OperationCatalog.Discover(typeof(ReferenceOperations));
        var generator = new SkillReferenceGenerator();
        var options = new SkillGenerationOptions("frontmatter-cli", "Description used for frontmatter validation", "frontmatter-cli");

        generator.Generate(catalog, _outputDirectory, options);

        var skill = File.ReadAllText(Path.Combine(_outputDirectory, "SKILL.md"));
        var lines = skill.Replace("\r\n", "\n").Split('\n');

        Assert.Equal("---", lines[0]);
        var closingIndex = Array.IndexOf(lines, "---", 1);
        Assert.True(closingIndex > 0, "Expected a closing '---' frontmatter delimiter.");

        var frontmatter = lines[1..closingIndex];
        Assert.Contains(frontmatter, line => line.StartsWith("name: \"", StringComparison.Ordinal) && line.Length > "name: \"\"".Length);
        Assert.Contains(frontmatter, line => line.StartsWith("description: \"", StringComparison.Ordinal) && line.Length > "description: \"\"".Length);
        Assert.Contains(frontmatter, line => line.StartsWith("executable: \"", StringComparison.Ordinal) && line.Length > "executable: \"\"".Length);
    }

    private static OperationCatalog BuildSyntheticCatalog(int categoryCount, int operationsPerCategory)
    {
        var builder = new OperationCatalogBuilder();
        for (var categoryIndex = 0; categoryIndex < categoryCount; categoryIndex++)
        {
            var category = categoryCount > 1 ? $"category{categoryIndex}" : null;
            for (var operationIndex = 0; operationIndex < operationsPerCategory; operationIndex++)
            {
                var name = $"category{categoryIndex}-op{operationIndex}";
                builder.Add(name, $"Synthetic operation {operationIndex} in category {categoryIndex}", () => "ok", configure: options =>
                {
                    options.Category = category;
                });
            }
        }

        return builder.Build();
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
