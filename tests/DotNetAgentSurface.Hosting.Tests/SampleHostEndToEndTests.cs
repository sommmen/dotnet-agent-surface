using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace DotNetAgentSurface.Hosting.Tests;

public sealed class SampleHostEndToEndTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Long_string_result_is_written_byte_identical_and_alone_on_stdout()
    {
        var result = await RunAsync("cli", "reports", "export-report", "--lines", "40");

        var expected = new StringBuilder("# Report\n\n");
        for (var line = 1; line <= 40; line++)
        {
            expected.Append("- line ").Append(line).Append(": \"quoted\" text with <markup> and unicode ✓\n");
        }

        Assert.Equal(0, result.ExitCode);
        Assert.True(expected.Length > 1000);
        Assert.Equal(new UTF8Encoding(false).GetBytes(expected.ToString()), result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task Logging_goes_to_stderr_and_stdout_holds_only_the_result()
    {
        var result = await RunAsync("cli", "reports", "noisy-report");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done", Encoding.UTF8.GetString(result.StdOut));
        Assert.Contains("diagnostic noise", Encoding.UTF8.GetString(result.StdErr));
    }

    [Fact]
    public async Task Operation_failure_exits_one_with_the_verbatim_multiline_error_on_stderr()
    {
        var result = await RunAsync("cli", "reports", "explain-failure");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Equal("Could not resolve the target.\nCandidates:\n  - alpha\n  - beta\n", Encoding.UTF8.GetString(result.StdErr));
    }

    [Fact]
    public async Task Usage_error_exits_two()
    {
        var result = await RunAsync("cli", "reports", "export-report", "--bogus");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Contains("Unknown flag '--bogus'", Encoding.UTF8.GetString(result.StdErr));
    }

    [Fact]
    public async Task Cancellation_exits_130()
    {
        var result = await RunAsync("cli", "reports", "cancel-me");

        Assert.Equal(130, result.ExitCode);
        Assert.Empty(result.StdOut);
    }

    [Fact]
    public async Task Async_only_disposable_singletons_dispose_cleanly()
    {
        var result = await RunAsync("cli", "reports", "export-report", "--lines", "1");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("InvalidOperationException", Encoding.UTF8.GetString(result.StdErr));
    }

    [Fact]
    public async Task Global_option_is_injected_into_operations_declaring_the_parameter()
    {
        var result = await RunAsync("cli", "--audit-name", "x", "reports", "export-report", "--lines", "1");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Cli_hides_mcp_only_operations()
    {
        var result = await RunAsync("cli", "reports", "load-context", "--name", "x");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Unknown operation 'load-context'", Encoding.UTF8.GetString(result.StdErr));
    }

    [Fact]
    public async Task Host_verb_runs_before_the_host_is_built()
    {
        var result = await RunAsync("version");

        Assert.Equal(0, result.ExitCode);
        Assert.NotEmpty(result.StdOut);
    }

    [Fact]
    public async Task Skill_generate_and_check_use_out_dir_and_document_global_options()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-surface-skill-{Guid.NewGuid():N}");
        try
        {
            var missing = await RunAsync("cli", "skill", "check", "--out-dir", directory);
            var generated = await RunAsync("cli", "skill", "generate", "--out-dir", directory);
            var current = await RunAsync("cli", "skill", "check", "--out-dir", directory);

            Assert.Equal(1, missing.ExitCode);
            Assert.Equal(0, generated.ExitCode);
            Assert.Equal(0, current.ExitCode);
            var skill = await File.ReadAllTextAsync(Path.Combine(directory, "SKILL.md"), TestContext.Current.CancellationToken);
            Assert.Contains("## Global options", skill);
            Assert.Contains("--audit-name <value>", skill);
            Assert.Contains("tasktracker cli", skill);
            var commands = await File.ReadAllTextAsync(Path.Combine(directory, "references", "commands", "reports.md"), TestContext.Current.CancellationToken);
            Assert.Contains("`--audit-name`", commands);
            Assert.DoesNotContain("load-context", commands);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Leading_global_flags_before_skill_and_help_are_skipped()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"agent-surface-skill-{Guid.NewGuid():N}");
        try
        {
            var generated = await RunAsync("cli", "--audit-name", "x", "skill", "generate", "--out-dir", directory);
            var help = await RunAsync("cli", "--audit-name", "x", "--help");

            Assert.Equal(0, generated.ExitCode);
            Assert.True(File.Exists(Path.Combine(directory, "SKILL.md")));
            Assert.Contains("Skill commands:", Encoding.UTF8.GetString(help.StdOut));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
    [Fact]
    public async Task Default_mode_serves_mcp_with_only_protocol_traffic_on_stdout()
    {
        const string input =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}\n" +
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"export-report\",\"arguments\":{\"lines\":1}}}\n";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        using var process = Process.Start(CreateStartInfo([]))!;
        using var killOnCancel = timeout.Token.Register(static state => KillTree((Process)state!), process);
        var stderr = ReadAllAsync(process.StandardError.BaseStream);
        await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token);
        await process.StandardInput.FlushAsync(timeout.Token);

        var lines = new List<string>();
        while (lines.Count < 3 && await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            lines.Add(line);
        }

        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        await stderr;        Assert.Equal(0, process.ExitCode);
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.StartsWith("{", line));
        var tools = Assert.Single(lines, line => line.Contains("\"id\":2", StringComparison.Ordinal));
        var call = Assert.Single(lines, line => line.Contains("\"id\":3", StringComparison.Ordinal));
        Assert.Contains("\"name\":\"load-context\"", tools);
        Assert.Contains("\"name\":\"export-report\"", tools);
        Assert.Contains("# Report", call);
    }

    private static async Task<RunResult> RunAsync(params string[] args)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(RunTimeout);
        using var process = Process.Start(CreateStartInfo(args))!;
        using var killOnCancel = timeout.Token.Register(static state => KillTree((Process)state!), process);
        process.StandardInput.Close();
        var stdout = ReadAllAsync(process.StandardOutput.BaseStream);
        var stderr = ReadAllAsync(process.StandardError.BaseStream);
        await process.WaitForExitAsync(timeout.Token);
        return new RunResult(process.ExitCode, await stdout, await stderr);
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
    private static ProcessStartInfo CreateStartInfo(string[] args)
    {
        var path = typeof(SampleHostEndToEndTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SampleHostPath").Value!;
        Assert.True(File.Exists(path), $"Sample host was not built at '{path}'.");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(path);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        return startInfo;
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed record RunResult(int ExitCode, byte[] StdOut, byte[] StdErr);
}
