using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.IO;
using System.Reflection;

namespace MyMcpServer;

public class Program
{
    private static string? _asbInstallPath;

    public static async Task Main(string[] args)
    {
        _asbInstallPath = AsbInstallationSettings.LoadInstallPath();
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAsbAssembly;

        var builder = Host.CreateApplicationBuilder(args);

        // stdout is reserved for JSON-RPC when using the stdio transport.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }

    private static Assembly? ResolveAsbAssembly(object? sender, ResolveEventArgs args)
    {
        var assemblyFileName = new AssemblyName(args.Name).Name + ".dll";
        var assemblyPath = Path.Combine(_asbInstallPath!, assemblyFileName);
        if (File.Exists(assemblyPath))
        {
            return Assembly.LoadFrom(assemblyPath);
        }

        return null;
    }
}
