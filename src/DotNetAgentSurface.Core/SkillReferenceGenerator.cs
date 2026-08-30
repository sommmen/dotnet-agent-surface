using System.Text;
using System.Text.Json;

namespace DotNetAgentSurface.Core;

public sealed class SkillReferenceGenerator
{
    private readonly OperationSchemaGenerator _schemaGenerator = new();

    public void Generate(OperationCatalog catalog, string outputDirectory)
    {
        Guard.ThrowIfNull(catalog);
        Guard.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(Path.Combine(outputDirectory, "SKILL.md"), RenderSkill(catalog), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputDirectory, "commands.md"), RenderCommands(catalog), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputDirectory, "schemas.json"), RenderSchemas(catalog), new UTF8Encoding(false));
    }

    public bool IsCurrent(OperationCatalog catalog, string outputDirectory)
    {
        Guard.ThrowIfNull(catalog);
        Guard.ThrowIfNullOrWhiteSpace(outputDirectory);

        return File.Exists(Path.Combine(outputDirectory, "SKILL.md"))
            && File.Exists(Path.Combine(outputDirectory, "commands.md"))
            && File.Exists(Path.Combine(outputDirectory, "schemas.json"))
            && File.ReadAllText(Path.Combine(outputDirectory, "SKILL.md")) == RenderSkill(catalog)
            && File.ReadAllText(Path.Combine(outputDirectory, "commands.md")) == RenderCommands(catalog)
            && File.ReadAllText(Path.Combine(outputDirectory, "schemas.json")) == RenderSchemas(catalog);
    }

    private static string RenderSkill(OperationCatalog catalog)
    {
        var builder = new StringBuilder("# Agent operations\n\nUse the following explicitly exposed operations. See [commands.md](commands.md) for the command reference.\n");
        foreach (var operation in catalog.Operations)
        {
            builder.Append($"\n## `{operation.Name}`\n\n{operation.Description}\n");
        }

        return builder.ToString();
    }

    private static string RenderCommands(OperationCatalog catalog)
    {
        var builder = new StringBuilder("# Command reference\n");
        foreach (var operation in catalog.Operations)
        {
            builder.Append($"\n## `{operation.Name}`\n\n{operation.Description}\n");
            foreach (var parameter in operation.Parameters.Where(static parameter => !parameter.IsCancellationToken))
            {
                builder.Append($"- `--{parameter.Name}` ({parameter.ParameterType.Name}){(parameter.IsOptional ? ", optional" : ", required")}\n");
            }
        }

        return builder.ToString();
    }

    private string RenderSchemas(OperationCatalog catalog)
    {
        var schemas = catalog.Operations.ToDictionary(
            static operation => operation.Name,
            operation => _schemaGenerator.GenerateInputSchema(operation).RootElement.Clone(),
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(schemas, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }
}
