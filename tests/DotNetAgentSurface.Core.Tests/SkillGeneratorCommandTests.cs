using DotNetAgentSurface.CommandLine;

namespace DotNetAgentSurface.Core.Tests;

public sealed class SkillGeneratorCommandTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(Path.GetTempPath(), $"dotnet-agent-surface-skill-cmd-{Guid.NewGuid():N}");

    [Fact]
    public async Task Generate_creates_expected_files()
    {
        var catalog = CreateCatalog();

        var result = await SkillGeneratorCommand.ExecuteAsync(["generate", "--output", _outputDirectory], catalog);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(_outputDirectory, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(_outputDirectory, "commands.md")));
        Assert.True(File.Exists(Path.Combine(_outputDirectory, "schemas.json")));
    }

    [Fact]
    public async Task Generate_without_force_is_a_noop_when_already_current()
    {
        var catalog = CreateCatalog();
        await SkillGeneratorCommand.ExecuteAsync(["generate", "--output", _outputDirectory], catalog);
        var writtenAt = File.GetLastWriteTimeUtc(Path.Combine(_outputDirectory, "SKILL.md"));

        await Task.Delay(25);
        var result = await SkillGeneratorCommand.ExecuteAsync(["generate", "--output", _outputDirectory], catalog);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("already current", result.Output);
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(Path.Combine(_outputDirectory, "SKILL.md")));
    }

    [Fact]
    public async Task Generate_with_force_regenerates_even_when_current()
    {
        var catalog = CreateCatalog();
        await SkillGeneratorCommand.ExecuteAsync(["generate", "--output", _outputDirectory], catalog);
        var writtenAt = File.GetLastWriteTimeUtc(Path.Combine(_outputDirectory, "SKILL.md"));

        await Task.Delay(25);
        var result = await SkillGeneratorCommand.ExecuteAsync(["generate", "--output", _outputDirectory, "--force"], catalog);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.GetLastWriteTimeUtc(Path.Combine(_outputDirectory, "SKILL.md")) >= writtenAt);
    }

    [Fact]
    public async Task Check_exits_zero_when_current_and_nonzero_when_stale_or_missing()
    {
        var catalog = CreateCatalog();

        var missing = await SkillGeneratorCommand.ExecuteAsync(["check", "--output", _outputDirectory], catalog);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("missing or stale", missing.Error);

        await SkillGeneratorCommand.ExecuteAsync(["generate", "--output", _outputDirectory], catalog);
        var current = await SkillGeneratorCommand.ExecuteAsync(["check", "--output", _outputDirectory], catalog);
        Assert.Equal(0, current.ExitCode);

        File.AppendAllText(Path.Combine(_outputDirectory, "SKILL.md"), "stale");
        var stale = await SkillGeneratorCommand.ExecuteAsync(["check", "--output", _outputDirectory], catalog);
        Assert.NotEqual(0, stale.ExitCode);
    }

    [Fact]
    public async Task Malformed_arguments_return_usage_error()
    {
        var catalog = CreateCatalog();

        var unknownVerb = await SkillGeneratorCommand.ExecuteAsync(["publish"], catalog);
        Assert.Equal(2, unknownVerb.ExitCode);

        var unknownFlag = await SkillGeneratorCommand.ExecuteAsync(["generate", "--bogus"], catalog);
        Assert.Equal(2, unknownFlag.ExitCode);

        var missingValue = await SkillGeneratorCommand.ExecuteAsync(["generate", "--output"], catalog);
        Assert.Equal(2, missingValue.ExitCode);
    }

    [Fact]
    public void CanHandle_recognizes_generate_and_check_but_not_help()
    {
        Assert.True(SkillGeneratorCommand.CanHandle(["generate"]));
        Assert.True(SkillGeneratorCommand.CanHandle(["check", "--output", "dir"]));
        Assert.False(SkillGeneratorCommand.CanHandle(["--help"]));
        Assert.False(SkillGeneratorCommand.CanHandle(["some-operation"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
    }

    private static OperationCatalog CreateCatalog() => OperationCatalog.Discover(typeof(ReferenceOperations));

    private sealed class ReferenceOperations
    {
        [AgentOperation("greet", "Greets a person")]
        public string Greet(string name) => $"Hello, {name}";
    }
}
