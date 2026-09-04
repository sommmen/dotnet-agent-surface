using Hangfire.SqlServer;
using Testcontainers.MsSql;

namespace DotNetAgentSurface.Hangfire.SqlServer.Tests;

/// <summary>
/// Provisions a throwaway SQL Server container for the opt-in compatibility suite.
/// The suite only starts a container - and only requires Docker - when the
/// <c>DOTNETAGENTSURFACE_HANGFIRE_SQLSERVER_TESTS</c> environment variable is set to
/// <c>1</c> or <c>true</c>. When unset, <see cref="IsEnabled"/> is <see langword="false"/> and
/// no container is started, keeping the default `dotnet test` run credential- and
/// Docker-free.
/// </summary>
public sealed class SqlServerCompatibilityFixture : IAsyncLifetime
{
    public const string OptInEnvironmentVariable = "DOTNETAGENTSURFACE_HANGFIRE_SQLSERVER_TESTS";

    private MsSqlContainer? _container;

    /// <summary>Gets a value indicating whether the opt-in environment variable enabled this suite.</summary>
    public bool IsEnabled { get; } = IsOptedIn();

    /// <summary>Gets the reason the suite is skipped, populated when <see cref="IsEnabled"/> is <see langword="false"/> or startup fails.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Gets the connection string for the running container. Only valid once initialized and enabled.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (!IsEnabled)
        {
            SkipReason =
                $"Set {OptInEnvironmentVariable}=1 (and ensure Docker is available) to run the opt-in " +
                "Hangfire SQL Server compatibility suite.";
            return;
        }

        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync().ConfigureAwait(false);
            ConnectionString = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            // Fail closed into a clean skip rather than a hard failure: the opt-in variable may be
            // set in an environment without a working Docker daemon (e.g. a misconfigured shell).
            SkipReason = $"Could not start a throwaway SQL Server container: {exception.Message}";
            _container = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a new <see cref="SqlServerStorage"/> pointed at the running container using
    /// Hangfire's default schema. Tests in this suite share a single collection fixture and
    /// therefore run sequentially against the same container, so they must use distinct
    /// recurring job IDs to avoid interfering with one another.
    /// </summary>
    public SqlServerStorage CreateStorage()
    {
        if (!IsEnabled || _container is null)
        {
            throw new InvalidOperationException(
                "The SQL Server compatibility container is not running. Check IsEnabled/SkipReason before calling CreateStorage.");
        }

        return new SqlServerStorage(ConnectionString, new SqlServerStorageOptions
        {
            PrepareSchemaIfNecessary = true,
        });
    }

    private static bool IsOptedIn()
    {
        var value = Environment.GetEnvironmentVariable(OptInEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCompatibilityCollection : ICollectionFixture<SqlServerCompatibilityFixture>
{
    public const string Name = "Hangfire SQL Server compatibility";
}
