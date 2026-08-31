if (args.Length == 1 && args[0] is "--version" or "-v" or "-V")
{
    var version = typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    Console.Out.WriteLine(version);
    return 0;
}

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.Out.WriteLine("DotNetAgentSurface.CommandLine exposes an application-provided operation catalog.");
    Console.Out.WriteLine("Configure a host with an OperationCatalog and service provider, then run <category> <operation> [--name JSON-value].");
    Console.Out.WriteLine("Use --help after a command path for available operations and flags.");
    return 0;
}

if (args.Length > 0)
{
    Console.Error.WriteLine($"Unknown host argument '{args[0]}'. Use --help for usage.");
    return 2;
}

Console.Error.WriteLine("This host requires an application-specific OperationCatalog and service provider.");
return 1;
