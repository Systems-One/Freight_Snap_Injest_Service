using FreightSnapIngestService.Options;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FreightSnapIngestService.Tests.Options;

public class FreightIngestOptionsTests
{
    [Fact]
    public void Binds_all_properties_from_configuration_section()
    {
        var json = """
        {
          "FreightIngest": {
            "CsvDirectory": "C:\\data\\csv",
            "ImageDirectory": "C:\\data\\images",
            "ProcessedDirectory": "C:\\data\\processed",
            "ErrorDirectory": "C:\\data\\errors",
            "PollIntervalSeconds": 10,
            "PendingTimeoutMinutes": 30,
            "RetentionDays": 30,
            "ConnectionString": "Server=.;Database=Freight;Trusted_Connection=True;"
          }
        }
        """;
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var config = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var options = new FreightIngestOptions();
        config.GetSection(FreightIngestOptions.SectionName).Bind(options);

        Assert.Equal(@"C:\data\csv", options.CsvDirectory);
        Assert.Equal(@"C:\data\images", options.ImageDirectory);
        Assert.Equal(@"C:\data\processed", options.ProcessedDirectory);
        Assert.Equal(@"C:\data\errors", options.ErrorDirectory);
        Assert.Equal(10, options.PollIntervalSeconds);
        Assert.Equal(30, options.PendingTimeoutMinutes);
        Assert.Equal(30, options.RetentionDays);
        Assert.Equal("Server=.;Database=Freight;Trusted_Connection=True;", options.ConnectionString);
    }
}
