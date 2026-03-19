using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using StewardMcp.Config;
using StewardMcp.Data;
using StewardMcp.Formation;
using StewardMcp.Services;

var builder = Host.CreateApplicationBuilder(args);

// All logging to stderr (stdout is the MCP JSON-RPC transport)
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Disabled;
});
builder.Logging.SetMinimumLevel(LogLevel.Information);
// Force stderr: the SimpleConsole logger respects LogToStandardErrorThreshold
builder.Services.Configure<Microsoft.Extensions.Logging.Console.ConsoleLoggerOptions>(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Configuration
builder.Services.AddSingleton<StewardConfig>();

// Data layer
builder.Services.AddSingleton<StewardDb>();
builder.Services.AddSingleton<VectorStore>();

// Services
builder.Services.AddSingleton<LlmService>();
builder.Services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<LlmService>());
builder.Services.AddScoped<WorkspaceService>();
builder.Services.AddSingleton<WorkspaceDbService>();

// Formation
builder.Services.AddSingleton<Canon>();
builder.Services.AddSingleton<TreeBuilder>();
builder.Services.AddSingleton<DossierBuilder>();
builder.Services.AddSingleton<ReflectionPipeline>();
builder.Services.AddSingleton<Scripture>();
builder.Services.AddSingleton<MindfulnessHandler>();

// MCP Server
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "Steward",
            Version = "0.1.0",
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly();

var host = builder.Build();

// Initialize
var logger = host.Services.GetRequiredService<ILogger<Program>>();
var config = host.Services.GetRequiredService<StewardConfig>();
config.EnsureDirectories();
logger.LogInformation("Data directory: {DataDir}", config.DataDir);
logger.LogInformation("Workspace directory: {WorkspaceDir}", config.WorkspaceDir);

var db = host.Services.GetRequiredService<StewardDb>();
await db.InitializeAsync();

var vectorStore = host.Services.GetRequiredService<VectorStore>();

// Try to initialize; if DuckDB is locked, kill stale instances and retry once
await vectorStore.InitializeAsync();
if (vectorStore.IsDisabled)
{
    var myPid = Environment.ProcessId;
    var staleKilled = false;
    foreach (var proc in Process.GetProcessesByName("Steward.Tools.MCP"))
    {
        if (proc.Id == myPid) continue;
        logger.LogWarning("Killing stale Steward process (PID {Pid})", proc.Id);
        try { proc.Kill(); proc.WaitForExit(3000); staleKilled = true; }
        catch (Exception ex) { logger.LogWarning(ex, "Could not kill PID {Pid}", proc.Id); }
    }
    if (staleKilled)
    {
        logger.LogInformation("Retrying VectorStore initialization after killing stale process...");
        await Task.Delay(1000);
        await vectorStore.InitializeAsync(retry: true);
    }
    if (vectorStore.IsDisabled)
        logger.LogWarning("Vector search is disabled — DuckDB file is locked. Journal and workspace tools still work.");
}

var canon = host.Services.GetRequiredService<Canon>();
canon.Bootstrap();

// Bootstrap Scripture as a mindfulness thread
await db.UpsertMindfulnessThreadAsync(new MindfulnessThread
{
    ThreadId = ReflectionConstants.ScriptureThreadId,
    Name = "Scripture",
    Prompt = "Scripture study — see Scripture.cs for reading plan logic",
    Probability = 0.10,
    Enabled = true,
});

// Resolve MindfulnessHandler to wire up the event handler (also resolves Scripture)
_ = host.Services.GetRequiredService<MindfulnessHandler>();

logger.LogInformation("StewardMCP server starting...");
await host.RunAsync();
