using FreightSnapIngestService;
using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Ingestion;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Retention;
using FreightSnapIngestService.Stability;
using Serilog;

// The Windows Service Control Manager launches this process with
// Environment.CurrentDirectory set to C:\Windows\System32, and AddWindowsService()
// does not change that. Relative paths (notably the Serilog file sink path in
// appsettings.json) must resolve next to the executable, not System32, so pin the
// working directory here before anything else (including host/config building) runs.
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "FreightSnapIngestService");

builder.Services.Configure<FreightIngestOptions>(
    builder.Configuration.GetSection(FreightIngestOptions.SectionName));

builder.Services.AddSerilog((_, loggerConfig) =>
    loggerConfig.ReadFrom.Configuration(builder.Configuration));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICsvScanParser, CsvScanParser>();
builder.Services.AddSingleton<IImageMatcher, ImageMatcher>();
builder.Services.AddSingleton<ScanFileNamer>();
builder.Services.AddSingleton<PendingScanTracker>();
builder.Services.AddSingleton<FileStabilityTracker>();
builder.Services.AddSingleton<IFreightScanRepository>(sp =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FreightIngestOptions>>().Value;
    return new FreightScanRepository(options.ConnectionString);
});
builder.Services.AddSingleton(sp => new IngestPipeline(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FreightIngestOptions>>().Value,
    sp.GetRequiredService<ICsvScanParser>(),
    sp.GetRequiredService<IImageMatcher>(),
    sp.GetRequiredService<ScanFileNamer>(),
    sp.GetRequiredService<PendingScanTracker>(),
    sp.GetRequiredService<FileStabilityTracker>(),
    sp.GetRequiredService<IFreightScanRepository>(),
    sp.GetRequiredService<ILogger<IngestPipeline>>()));
builder.Services.AddSingleton(sp => new RetentionCleaner(
    sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FreightIngestOptions>>().Value,
    sp.GetRequiredService<IFreightScanRepository>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<RetentionCleaner>>()));

builder.Services.AddHostedService<IngestWorker>();
builder.Services.AddHostedService<RetentionWorker>();

var host = builder.Build();

var resolvedOptions = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FreightIngestOptions>>().Value;
var startupLogger = host.Services.GetRequiredService<ILogger<Program>>();
StartupValidation.LogConfiguration(resolvedOptions, startupLogger);

host.Run();
