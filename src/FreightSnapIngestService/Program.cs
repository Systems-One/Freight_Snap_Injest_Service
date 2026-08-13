using FreightSnapIngestService.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<FreightIngestOptions>(
    builder.Configuration.GetSection(FreightIngestOptions.SectionName));

builder.Services.AddSerilog((services, loggerConfig) =>
    loggerConfig.ReadFrom.Configuration(builder.Configuration));

var host = builder.Build();
host.Run();
