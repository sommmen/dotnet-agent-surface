using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotNetAgentSurface.Core;

public sealed record SkillGenerationOptions
{
    public string SkillName { get; }
    public string SkillDescription { get; }
    public string ExecutableName { get; }
    public int CategoryShardThreshold { get; } = 20;

    public SkillGenerationOptions(string skillName, string skillDescription, string executableName, int categoryShardThreshold = 20)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            throw new ArgumentException("Skill name cannot be null or whitespace.", nameof(skillName));
        }

        if (string.IsNullOrWhiteSpace(skillDescription))
        {
            throw new ArgumentException("Skill description cannot be null or whitespace.", nameof(skillDescription));
        }

        if (string.IsNullOrWhiteSpace(executableName))
        {
            throw new ArgumentException("Executable name cannot be null or whitespace.", nameof(executableName));
        }

        if (categoryShardThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(categoryShardThreshold), "Category shard threshold must be at least 1.");
        }

        SkillName = skillName;
        SkillDescription = skillDescription;
        ExecutableName = executableName;
        CategoryShardThreshold = categoryShardThreshold;
    }
}

public sealed class SkillReferenceGenerator
{
    private readonly OperationSchemaGenerator _schemaGenerator = new();

    public void Generate(OperationCatalog catalog, string outputDirectory)
        => Generate(catalog, outputDirectory, DefaultOptions(outputDirectory));

    public void Generate(OperationCatalog catalog, string outputDirectory, SkillGenerationOptions options)
    {
        Guard.ThrowIfNull(catalog);
        Guard.ThrowIfNullOrWhiteSpace(outputDirectory);
        var effectiveOptions = options ?? DefaultOptions(outputDirectory);

        var referenceDirectory = Path.Combine(outputDirectory, "references");
        var commandDirectory = Path.Combine(referenceDirectory, "commands");
        Directory.CreateDirectory(outputDirectory);
        Directory.CreateDirectory(referenceDirectory);
        Directory.CreateDirectory(commandDirectory);

        var expectedFiles = GetExpectedFiles(catalog, effectiveOptions);
        foreach (var currentFile in Directory.EnumerateFiles(commandDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = ToRelativePath(outputDirectory, currentFile).Replace('\\', '/');
            if (!expectedFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
            {
                File.Delete(currentFile);
            }
        }

        foreach (var staleDirectory in Directory.EnumerateDirectories(commandDirectory, "*", SearchOption.AllDirectories))
        {
            if (!Directory.EnumerateFileSystemEntries(staleDirectory).Any())
            {
                Directory.Delete(staleDirectory, recursive: false);
            }
        }

        File.WriteAllText(Path.Combine(outputDirectory, "SKILL.md"), RenderSkill(catalog, effectiveOptions), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(referenceDirectory, "commands.md"), RenderCommandsReference(catalog, effectiveOptions), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(referenceDirectory, "schemas.json"), RenderSchemas(catalog), new UTF8Encoding(false));

        foreach (var relativePath in expectedFiles.Where(static path => path.StartsWith("references/commands/", StringComparison.OrdinalIgnoreCase)))
        {
            var fullPath = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(fullPath, RenderCategoryFile(catalog, effectiveOptions, relativePath), new UTF8Encoding(false));
        }
    }

    public bool IsCurrent(OperationCatalog catalog, string outputDirectory)
        => IsCurrent(catalog, outputDirectory, DefaultOptions(outputDirectory));

    public bool IsCurrent(OperationCatalog catalog, string outputDirectory, SkillGenerationOptions options)
    {
        Guard.ThrowIfNull(catalog);
        Guard.ThrowIfNullOrWhiteSpace(outputDirectory);
        var effectiveOptions = options ?? DefaultOptions(outputDirectory);
        var expectedFiles = GetExpectedFiles(catalog, effectiveOptions);

        if (!File.Exists(Path.Combine(outputDirectory, "SKILL.md")))
        {
            return false;
        }

        if (File.ReadAllText(Path.Combine(outputDirectory, "SKILL.md")) != RenderSkill(catalog, effectiveOptions))
        {
            return false;
        }

        foreach (var relativePath in expectedFiles)
        {
            var fullPath = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                return false;
            }

            if (relativePath == "SKILL.md" || relativePath == "references/commands.md" || relativePath == "references/schemas.json")
            {
                var expectedContent = relativePath switch
                {
                    "SKILL.md" => RenderSkill(catalog, effectiveOptions),
                    "references/commands.md" => RenderCommandsReference(catalog, effectiveOptions),
                    "references/schemas.json" => RenderSchemas(catalog),
                    _ => throw new InvalidOperationException($"Unexpected file '{relativePath}'.")
                };

                if (File.ReadAllText(fullPath) != expectedContent)
                {
                    return false;
                }
            }
            else if (relativePath.StartsWith("references/commands/", StringComparison.OrdinalIgnoreCase))
            {
                var expectedContent = RenderCategoryFile(catalog, effectiveOptions, relativePath);
                if (File.ReadAllText(fullPath) != expectedContent)
                {
                    return false;
                }
            }
        }

        var commandDirectory = Path.Combine(outputDirectory, "references", "commands");
        if (Directory.Exists(commandDirectory))
        {
            foreach (var actualFile in Directory.EnumerateFiles(commandDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = ToRelativePath(outputDirectory, actualFile).Replace('\\', '/');
                if (!expectedFiles.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static SkillGenerationOptions DefaultOptions(string outputDirectory)
    {
        var executable = SanitizeExecutableName(outputDirectory);
        var skillName = SanitizeSkillName(executable);
        return new SkillGenerationOptions(skillName, $"Operations exposed by the {executable} CLI.", executable);
    }

    private static string SanitizeExecutableName(string outputDirectory)
    {
        var lastSegment = Path.GetFileName(outputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(lastSegment))
        {
            return "skill";
        }

        return Regex.Replace(lastSegment, "[^A-Za-z0-9._-]+", "-").Trim('-');
    }

    private static string SanitizeSkillName(string executableName) => string.IsNullOrWhiteSpace(executableName) ? "skill" : executableName;

    private static string RenderSkill(OperationCatalog catalog, SkillGenerationOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine($"name: \"{EscapeYaml(options.SkillName)}\"");
        builder.AppendLine($"description: \"{EscapeYaml(options.SkillDescription)}\"");
        builder.AppendLine($"executable: \"{EscapeYaml(options.ExecutableName)}\"");
        builder.AppendLine("---");
        builder.AppendLine();
        builder.AppendLine($"# {options.SkillName}");
        builder.AppendLine();
        builder.AppendLine($"Use this skill when you need to invoke generated operations via the `{options.ExecutableName}` CLI.");
        builder.AppendLine($"Run `{options.ExecutableName} --help` to list available commands, then read the detailed reference in [references/commands.md](references/commands.md) when you need parameter details or examples.");
        builder.AppendLine();
        builder.AppendLine("## Command index");
        builder.AppendLine();

        var indexLines = BuildCommandIndex(catalog, options);
        if (indexLines.Count == 0)
        {
            builder.AppendLine("No operations are currently exposed by this skill.");
        }
        else
        {
            foreach (var line in indexLines)
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Examples");
        builder.AppendLine();

        var examples = catalog.Operations
            .SelectMany(operation => operation.Examples.Select(example => ($"{example}", operation.Name)))
            .Take(3)
            .ToList();

        if (examples.Count == 0)
        {
            builder.AppendLine("- `" + options.ExecutableName + " --help`");
        }
        else
        {
            foreach (var (example, operationName) in examples)
            {
                builder.AppendLine($"- `{options.ExecutableName} {operationName} {example.Trim()}`");
            }
        }

        builder.AppendLine();
        builder.AppendLine("See [references/commands.md](references/commands.md) for the full generated reference.");
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string RenderCommandsReference(OperationCatalog catalog, SkillGenerationOptions options)
    {
        if (!ShouldShard(catalog, options.CategoryShardThreshold))
        {
            return RenderFullCommands(catalog, options);
        }

        var builder = new StringBuilder();
        builder.AppendLine("# Command reference");
        builder.AppendLine();

        foreach (var group in BuildCommandGroups(catalog, options))
        {
            builder.AppendLine($"- [{group.Title}]({GetCommandReferenceLink(group.Category)}) — {group.Operations.Count} operation{(group.Operations.Count == 1 ? string.Empty : "s")}");
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string RenderFullCommands(OperationCatalog catalog, SkillGenerationOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Command reference");
        builder.AppendLine();

        foreach (var operation in catalog.Operations)
        {
            builder.AppendLine($"## `{operation.Name}`");
            builder.AppendLine();
            builder.AppendLine(operation.Description);
            builder.AppendLine();
            builder.AppendLine($"- Safety: `{operation.SafetyLevel}`");
            builder.AppendLine($"- Idempotent: `{(operation.IsIdempotent ? "yes" : "no")}`");
            if (!string.IsNullOrWhiteSpace(operation.Category))
            {
                builder.AppendLine($"- Category: `{operation.Category}`");
            }

            var parameters = operation.Parameters.Where(static parameter => !parameter.IsCancellationToken).ToArray();
            if (parameters.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("### Parameters");
                foreach (var parameter in parameters)
                {
                    builder.AppendLine($"- `--{parameter.Name}` ({parameter.ParameterType.Name}){(parameter.IsOptional ? ", optional" : ", required")}");
                }
            }

            if (operation.Aliases.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine($"- Aliases: {string.Join(", ", operation.Aliases.Select(static alias => $"`{alias}`"))}");
            }

            if (operation.Examples.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("### Examples");
                foreach (var example in operation.Examples)
                {
                    builder.AppendLine($"- `{options.ExecutableName} {operation.Name} {example.Trim()}`");
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string RenderCategoryFile(OperationCatalog catalog, SkillGenerationOptions options, string relativePath)
    {
        var categoryKey = GetRelativeCategoryKey(relativePath);
        var categoryOperations = catalog.Operations
            .Where(static operation => !string.IsNullOrWhiteSpace(operation.Category))
            .Where(operation => GetCategorySlug(operation.Category) == categoryKey)
            .ToArray();

        if (categoryKey == "_root")
        {
            categoryOperations = catalog.Operations
                .Where(static operation => string.IsNullOrWhiteSpace(operation.Category))
                .ToArray();
        }

        var builder = new StringBuilder();
        var title = categoryKey == "_root" ? "Uncategorized operations" : categoryKey.Replace('-', ' ');
        builder.AppendLine($"# {title}");
        builder.AppendLine();

        foreach (var operation in categoryOperations)
        {
            builder.AppendLine($"## `{operation.Name}`");
            builder.AppendLine();
            builder.AppendLine(operation.Description);
            builder.AppendLine();
            builder.AppendLine($"- Safety: `{operation.SafetyLevel}`");
            builder.AppendLine($"- Idempotent: `{(operation.IsIdempotent ? "yes" : "no")}`");

            var parameters = operation.Parameters.Where(static parameter => !parameter.IsCancellationToken).ToArray();
            if (parameters.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("### Parameters");
                foreach (var parameter in parameters)
                {
                    builder.AppendLine($"- `--{parameter.Name}` ({parameter.ParameterType.Name}){(parameter.IsOptional ? ", optional" : ", required")}");
                }
            }

            if (operation.Examples.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("### Examples");
                foreach (var example in operation.Examples)
                {
                    builder.AppendLine($"- `{options.ExecutableName} {operation.Name} {example.Trim()}`");
                }
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    private string RenderSchemas(OperationCatalog catalog)
    {
        var schemas = catalog.Operations.ToDictionary(
            static operation => operation.Name,
            operation => _schemaGenerator.GenerateInputSchema(operation).RootElement.Clone(),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(schemas, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    private static bool ShouldShard(OperationCatalog catalog, int threshold)
    {
        var distinctCategories = catalog.Operations
            .Select(static operation => operation.Category)
            .Where(static category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        return catalog.Operations.Count > threshold || distinctCategories > 1;
    }

    private static List<string> BuildCommandIndex(OperationCatalog catalog, SkillGenerationOptions options)
    {
        if (ShouldShard(catalog, options.CategoryShardThreshold))
        {
            return BuildCommandGroups(catalog, options)
                .Select(static group => $"- [{group.Title}]({GetCommandReferenceLink(group.Category)}) — {group.Operations.Count} operation{(group.Operations.Count == 1 ? string.Empty : "s")}")
                .ToList();
        }

        return catalog.Operations
            .Select(static operation => $"- [`{operation.Name}`](references/commands.md) — {operation.Description}")
            .ToList();
    }

    private static IReadOnlyList<(string Title, string Category, IReadOnlyList<OperationDescriptor> Operations)> BuildCommandGroups(OperationCatalog catalog, SkillGenerationOptions options)
    {
        var groups = new List<(string Title, string Category, List<OperationDescriptor> Operations)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var operation in catalog.Operations)
        {
            var category = operation.Category ?? string.Empty;
            var categoryKey = string.IsNullOrWhiteSpace(category) ? "_root" : GetCategorySlug(category);
            if (!seen.Add(categoryKey))
            {
                continue;
            }

            var title = categoryKey == "_root" ? "Uncategorized" : category.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(static segment => char.ToUpperInvariant(segment[0]) + segment[1..]).Aggregate(string.Empty, (current, next) => string.IsNullOrEmpty(current) ? next : current + " / " + next);
            groups.Add((title, categoryKey, new List<OperationDescriptor>()));
        }

        foreach (var operation in catalog.Operations)
        {
            var categoryKey = string.IsNullOrWhiteSpace(operation.Category) ? "_root" : GetCategorySlug(operation.Category);
            var group = groups.FirstOrDefault(item => string.Equals(item.Category, categoryKey, StringComparison.OrdinalIgnoreCase));
            if (group.Category != null)
            {
                groups.First(item => string.Equals(item.Category, categoryKey, StringComparison.OrdinalIgnoreCase)).Operations.Add(operation);
            }
        }

        var ordered = groups
            .OrderBy(static item => item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(static item => (item.Title, item.Category, (IReadOnlyList<OperationDescriptor>)item.Operations.OrderBy(static operation => operation.Name, StringComparer.Ordinal).ToArray()))
            .ToArray();

        return ordered;
    }

    private static string GetCategorySlug(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return "_root";
        }

        var segments = OperationCatalog.GetCategorySegments(category).Select(static segment => Regex.Replace(segment, "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant())
            .Where(static segment => !string.IsNullOrEmpty(segment));
        return string.Join("-", segments);
    }

    private static string GetCommandReferenceLink(string categoryKey)
    {
        return categoryKey == "_root"
            ? "commands/_root.md"
            : $"commands/{categoryKey}.md";
    }

    private static string GetRelativeCategoryKey(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        return string.Equals(fileName, "_root.md", StringComparison.OrdinalIgnoreCase) ? "_root" : Path.GetFileNameWithoutExtension(fileName);
    }

    private static HashSet<string> GetExpectedFiles(OperationCatalog catalog, SkillGenerationOptions options)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SKILL.md",
            "references/commands.md",
            "references/schemas.json"
        };

        if (ShouldShard(catalog, options.CategoryShardThreshold))
        {
            foreach (var group in BuildCommandGroups(catalog, options))
            {
                files.Add(Path.Combine("references","commands", group.Category + ".md").Replace('\\', '/'));
            }
        }

        return files;
    }

    private static string ToRelativePath(string rootPath, string fullPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(fullPath);

        var rootUri = new Uri(root);
        var candidateUri = new Uri(candidate);
        var relativeUri = rootUri.MakeRelativeUri(candidateUri);
        return Uri.UnescapeDataString(relativeUri.ToString()).Replace('/', Path.DirectorySeparatorChar);
    }

    private static string EscapeYaml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
