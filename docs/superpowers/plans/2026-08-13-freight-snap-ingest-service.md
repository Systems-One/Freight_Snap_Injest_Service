# Freight Snap Ingest Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a .NET 8 Windows Service that ingests CSV scan records and matched barcode images from two watch directories, writes them to SQL Server, moves them into a processed folder, and cleans up old output on a retention schedule while preserving scan data permanently.

**Architecture:** A Worker Service host running two independent `BackgroundService` timer loops — an ingest loop that polls, parses, matches, and finalizes CSV+image sets, and a retention loop that deletes expired output and clears the corresponding database path columns. Each piece of logic (CSV parsing, image matching, file naming, pending-timeout tracking, file-stability detection, DB access) is a small, independently testable class wired together by an orchestrating `IngestPipeline`.

**Tech Stack:** .NET 8, `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.Data.SqlClient` (plain ADO.NET, no ORM), Serilog (`Serilog.Extensions.Hosting`, `Serilog.Sinks.File`, `Serilog.Sinks.Console`), xUnit + `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) for tests.

**Spec:** `docs/superpowers/specs/2026-08-13-freight-snap-ingest-service-design.md`

## Global Constraints

- CSV files are **headerless**, one data line, columns positional in this exact order: `Barcode, DateTime, Length, Width, Height, Volume, Weight` (confirmed against real production CSVs in `Demo_Data/SWAD_FILES/`, e.g. `724076,20260804144415,122.0,101.5,128.5,1.591216,216.000`). `DateTime` is a compact 14-digit `yyyyMMddHHmmss` string with no separators, not ISO-8601. The barcode field is treated as an opaque string with no format validation — noisy/garbage-prefixed barcodes are accepted as-is and simply won't match any image.
- `ImageMatcher` barcode-in-filename matching must be boundary-bounded, not a raw substring test: shorter barcodes are literal substrings of longer ones in real data (e.g. `555` is a substring of `5555`), so a matched barcode must be delimited by a non-digit character (or string start/end) on both sides in the filename.
- Poll interval default: 10 seconds (`PollIntervalSeconds`). Pending-image timeout default: 30 minutes (`PendingTimeoutMinutes`). Retention default: 30 days (`RetentionDays`). Retention loop runs hourly.
- A CSV is only read once its size is unchanged across two consecutive polls.
- Processed folder name: `<Barcode>_<DateTime:yyyyMMddHHmmss>`. Renamed CSV: `<Barcode>_<DateTime:yyyyMMddHHmmss>.csv`. Renamed images: `<Barcode>_<DateTime:yyyyMMddHHmmss>_<n><original-extension>`, `n` starting at 1.
- Malformed CSVs move to `ErrorDirectory` immediately, are logged, and are never retried.
- DB insert failure after a file move: undo the move (files go back to their original directory under their original names) and retry on the next poll. No separate quarantine path for DB failures.
- Every scan is inserted as a new row — no dedup, no update-on-duplicate-barcode.
- Retention deletes files and DB path rows/columns (`FreightScanImages` rows, `FreightScans.CsvPath`) but never deletes `FreightScans` rows themselves.
- No FTP/API upload code — the database is the only integration surface for downstream consumers.

---

## File Structure

```
FreightSnapIngestService.sln
src/FreightSnapIngestService/
  FreightSnapIngestService.csproj
  Program.cs
  appsettings.json
  Options/
    FreightIngestOptions.cs
  Models/
    ScanRecord.cs
  Csv/
    CsvScanParser.cs
    CsvParseException.cs
  Images/
    ImageMatcher.cs
  Naming/
    ScanFileNamer.cs
  Pending/
    PendingScanTracker.cs
  Stability/
    FileStabilityTracker.cs
  Db/
    SchemaSql.cs
    FreightScanRepository.cs
  Ingestion/
    IngestPipeline.cs
    IngestWorker.cs
  Retention/
    RetentionCleaner.cs
    RetentionWorker.cs
db/
  schema.sql
tests/FreightSnapIngestService.Tests/
  FreightSnapIngestService.Tests.csproj
  Csv/CsvScanParserTests.cs
  Images/ImageMatcherTests.cs
  Naming/ScanFileNamerTests.cs
  Pending/PendingScanTrackerTests.cs
  Stability/FileStabilityTrackerTests.cs
  Db/FreightScanRepositoryTests.cs
  Db/SqlServerTestFixture.cs
  Ingestion/IngestPipelineTests.cs
  Ingestion/IngestWorkerTests.cs
  Retention/RetentionCleanerTests.cs
  Retention/RetentionWorkerTests.cs
```

---

### Task 1: Project scaffolding and configuration options

**Files:**
- Create: `FreightSnapIngestService.sln`
- Create: `src/FreightSnapIngestService/FreightSnapIngestService.csproj`
- Create: `src/FreightSnapIngestService/appsettings.json`
- Create: `src/FreightSnapIngestService/Options/FreightIngestOptions.cs`
- Create: `src/FreightSnapIngestService/Program.cs`
- Create: `tests/FreightSnapIngestService.Tests/FreightSnapIngestService.Tests.csproj`
- Test: `tests/FreightSnapIngestService.Tests/Options/FreightIngestOptionsTests.cs`

**Interfaces:**
- Produces: `FreightIngestOptions` class with public settable properties `string CsvDirectory`, `string ImageDirectory`, `string ProcessedDirectory`, `string ErrorDirectory`, `int PollIntervalSeconds`, `int PendingTimeoutMinutes`, `int RetentionDays`, `string ConnectionString`; and `const string SectionName = "FreightIngest"`.

- [ ] **Step 1: Create the solution and project files**

```bash
mkdir -p src/FreightSnapIngestService tests/FreightSnapIngestService.Tests
dotnet new worker -n FreightSnapIngestService -o src/FreightSnapIngestService
dotnet new xunit -n FreightSnapIngestService.Tests -o tests/FreightSnapIngestService.Tests
dotnet new sln -n FreightSnapIngestService
dotnet sln add src/FreightSnapIngestService/FreightSnapIngestService.csproj
dotnet sln add tests/FreightSnapIngestService.Tests/FreightSnapIngestService.Tests.csproj
dotnet add tests/FreightSnapIngestService.Tests/FreightSnapIngestService.Tests.csproj reference src/FreightSnapIngestService/FreightSnapIngestService.csproj
```

- [ ] **Step 2: Add NuGet packages**

```bash
dotnet add src/FreightSnapIngestService/FreightSnapIngestService.csproj package Microsoft.Data.SqlClient
dotnet add src/FreightSnapIngestService/FreightSnapIngestService.csproj package Microsoft.Extensions.Hosting.WindowsServices
dotnet add src/FreightSnapIngestService/FreightSnapIngestService.csproj package Serilog.Extensions.Hosting
dotnet add src/FreightSnapIngestService/FreightSnapIngestService.csproj package Serilog.Sinks.File
dotnet add src/FreightSnapIngestService/FreightSnapIngestService.csproj package Serilog.Sinks.Console
dotnet add src/FreightSnapIngestService/FreightSnapIngestService.csproj package Serilog.Settings.Configuration
dotnet add tests/FreightSnapIngestService.Tests/FreightSnapIngestService.Tests.csproj package Microsoft.Extensions.TimeProvider.Testing
```

- [ ] **Step 3: Write the failing test for options binding**

```csharp
// tests/FreightSnapIngestService.Tests/Options/FreightIngestOptionsTests.cs
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
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter FreightIngestOptionsTests`
Expected: FAIL (compile error — `FreightIngestOptions` does not exist)

- [ ] **Step 5: Implement `FreightIngestOptions`**

```csharp
// src/FreightSnapIngestService/Options/FreightIngestOptions.cs
namespace FreightSnapIngestService.Options;

public class FreightIngestOptions
{
    public const string SectionName = "FreightIngest";

    public string CsvDirectory { get; set; } = "";
    public string ImageDirectory { get; set; } = "";
    public string ProcessedDirectory { get; set; } = "";
    public string ErrorDirectory { get; set; } = "";
    public int PollIntervalSeconds { get; set; } = 10;
    public int PendingTimeoutMinutes { get; set; } = 30;
    public int RetentionDays { get; set; } = 30;
    public string ConnectionString { get; set; } = "";
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter FreightIngestOptionsTests`
Expected: PASS

- [ ] **Step 7: Write `appsettings.json` and a minimal `Program.cs`**

```json
// src/FreightSnapIngestService/appsettings.json
{
  "FreightIngest": {
    "CsvDirectory": "C:\\FreightSnap\\incoming\\csv",
    "ImageDirectory": "C:\\FreightSnap\\incoming\\images",
    "ProcessedDirectory": "C:\\FreightSnap\\processed",
    "ErrorDirectory": "C:\\FreightSnap\\errors",
    "PollIntervalSeconds": 10,
    "PendingTimeoutMinutes": 30,
    "RetentionDays": 30,
    "ConnectionString": "Server=.;Database=FreightSnapIngest;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "Serilog": {
    "MinimumLevel": "Information",
    "WriteTo": [
      { "Name": "Console" },
      {
        "Name": "File",
        "Args": {
          "path": "logs/freight-snap-ingest-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 30
        }
      }
    ]
  }
}
```

```csharp
// src/FreightSnapIngestService/Program.cs
using FreightSnapIngestService.Options;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.Configure<FreightIngestOptions>(
    builder.Configuration.GetSection(FreightIngestOptions.SectionName));

builder.Services.AddSerilog((services, loggerConfig) =>
    loggerConfig.ReadFrom.Configuration(builder.Configuration));

var host = builder.Build();
host.Run();
```

- [ ] **Step 8: Build to verify everything compiles**

Run: `dotnet build FreightSnapIngestService.sln`
Expected: Build succeeded

- [ ] **Step 9: Commit**

```bash
git add .
git commit -m "chore: scaffold FreightSnapIngestService project and configuration options"
```

---

### Task 2: CSV scan parsing

**Files:**
- Create: `src/FreightSnapIngestService/Models/ScanRecord.cs`
- Create: `src/FreightSnapIngestService/Csv/CsvParseException.cs`
- Create: `src/FreightSnapIngestService/Csv/CsvScanParser.cs`
- Test: `tests/FreightSnapIngestService.Tests/Csv/CsvScanParserTests.cs`

**Interfaces:**
- Produces: `record ScanRecord(string Barcode, DateTime ScanDateTime, decimal? Length, decimal? Width, decimal? Height, decimal? Volume, decimal? Weight)`.
- Produces: `class CsvParseException(string message) : Exception(message)`.
- Produces: `interface ICsvScanParser { ScanRecord Parse(string csvContent); }` implemented by `class CsvScanParser : ICsvScanParser`.

Real production CSVs (see `Demo_Data/SWAD_FILES/*.csv`) are **headerless**: one data line, fields positional in the fixed order `Barcode, DateTime, Length, Width, Height, Volume, Weight`, e.g. `724076,20260804144415,122.0,101.5,128.5,1.591216,216.000`. `DateTime` is `yyyyMMddHHmmss` (14 digits, no separators). The barcode field is accepted as-is with no format validation — noisy data (e.g. extra leading characters) is a valid barcode string as far as this parser is concerned.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/FreightSnapIngestService.Tests/Csv/CsvScanParserTests.cs
using FreightSnapIngestService.Csv;
using Xunit;

namespace FreightSnapIngestService.Tests.Csv;

public class CsvScanParserTests
{
    private readonly CsvScanParser _parser = new();

    [Fact]
    public void Parses_a_valid_headerless_row_into_a_ScanRecord()
    {
        var csv = "724076,20260804144415,122.0,101.5,128.5,1.591216,216.000\n";

        var result = _parser.Parse(csv);

        Assert.Equal("724076", result.Barcode);
        Assert.Equal(new DateTime(2026, 8, 4, 14, 44, 15), result.ScanDateTime);
        Assert.Equal(122.0m, result.Length);
        Assert.Equal(101.5m, result.Width);
        Assert.Equal(128.5m, result.Height);
        Assert.Equal(1.591216m, result.Volume);
        Assert.Equal(216.000m, result.Weight);
    }

    [Fact]
    public void Accepts_a_barcode_field_with_extra_leading_characters_as_is()
    {
        var csv = "vvhjmfvjhfg724076,20260804144524,122.0,101.5,128.5,1.591216,216.000\n";

        var result = _parser.Parse(csv);

        Assert.Equal("vvhjmfvjhfg724076", result.Barcode);
    }

    [Fact]
    public void Throws_CsvParseException_when_a_field_is_missing()
    {
        var csv = "724076,20260804144415,122.0,101.5,128.5,1.591216\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("6 field", ex.Message);
    }

    [Fact]
    public void Throws_CsvParseException_when_there_is_no_data_row()
    {
        var csv = "\n\n";

        Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
    }

    [Fact]
    public void Throws_CsvParseException_when_a_numeric_field_is_not_a_number()
    {
        var csv = "724076,20260804144415,not-a-number,101.5,128.5,1.591216,216.000\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("Length", ex.Message);
    }

    [Fact]
    public void Throws_CsvParseException_when_the_barcode_field_is_empty()
    {
        var csv = ",20260804144415,122.0,101.5,128.5,1.591216,216.000\n";

        Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
    }

    [Fact]
    public void Throws_CsvParseException_when_the_datetime_is_not_in_the_expected_format()
    {
        var csv = "724076,2026-08-04T14:44:15,122.0,101.5,128.5,1.591216,216.000\n";

        var ex = Assert.Throws<CsvParseException>(() => _parser.Parse(csv));
        Assert.Contains("DateTime", ex.Message);
    }

    [Fact]
    public void Skips_blank_lines_and_uses_the_first_non_blank_data_row()
    {
        var csv = "\n724076,20260804144415,122.0,101.5,128.5,1.591216,216.000\n\n";

        var result = _parser.Parse(csv);

        Assert.Equal("724076", result.Barcode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter CsvScanParserTests`
Expected: FAIL (compile error — types don't exist yet)

- [ ] **Step 3: Implement `ScanRecord` and `CsvParseException`**

```csharp
// src/FreightSnapIngestService/Models/ScanRecord.cs
namespace FreightSnapIngestService.Models;

public record ScanRecord(
    string Barcode,
    DateTime ScanDateTime,
    decimal? Length,
    decimal? Width,
    decimal? Height,
    decimal? Volume,
    decimal? Weight);
```

```csharp
// src/FreightSnapIngestService/Csv/CsvParseException.cs
namespace FreightSnapIngestService.Csv;

public class CsvParseException : Exception
{
    public CsvParseException(string message) : base(message) { }
}
```

- [ ] **Step 4: Implement `CsvScanParser`**

```csharp
// src/FreightSnapIngestService/Csv/CsvScanParser.cs
using System.Globalization;
using FreightSnapIngestService.Models;

namespace FreightSnapIngestService.Csv;

public interface ICsvScanParser
{
    ScanRecord Parse(string csvContent);
}

public class CsvScanParser : ICsvScanParser
{
    private const int ExpectedFieldCount = 7;
    private const string DateTimeFormat = "yyyyMMddHHmmss";

    public ScanRecord Parse(string csvContent)
    {
        var dataLine = csvContent
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

        if (dataLine is null)
            throw new CsvParseException("CSV has no data row.");

        var fields = dataLine.Split(',').Select(f => f.Trim()).ToArray();
        if (fields.Length != ExpectedFieldCount)
            throw new CsvParseException(
                $"CSV data row has {fields.Length} field(s); expected {ExpectedFieldCount} " +
                "(Barcode, DateTime, Length, Width, Height, Volume, Weight).");

        var barcode = fields[0];
        if (string.IsNullOrWhiteSpace(barcode))
            throw new CsvParseException("Barcode field is empty.");

        var dateTimeRaw = fields[1];
        if (!DateTime.TryParseExact(
                dateTimeRaw, DateTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var scanDateTime))
            throw new CsvParseException($"DateTime field value '{dateTimeRaw}' is not a valid '{DateTimeFormat}' date/time.");

        decimal? ParseDecimal(string fieldName, string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new CsvParseException($"{fieldName} field value '{raw}' is not a valid number.");
            return value;
        }

        return new ScanRecord(
            barcode,
            scanDateTime,
            ParseDecimal("Length", fields[2]),
            ParseDecimal("Width", fields[3]),
            ParseDecimal("Height", fields[4]),
            ParseDecimal("Volume", fields[5]),
            ParseDecimal("Weight", fields[6]));
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter CsvScanParserTests`
Expected: PASS (8 tests)

- [ ] **Step 6: Commit**

```bash
git add src/FreightSnapIngestService/Models src/FreightSnapIngestService/Csv tests/FreightSnapIngestService.Tests/Csv
git commit -m "feat: parse freight scan CSV rows into ScanRecord"
```

---

### Task 3: Image matching by barcode

**Files:**
- Create: `src/FreightSnapIngestService/Images/ImageMatcher.cs`
- Test: `tests/FreightSnapIngestService.Tests/Images/ImageMatcherTests.cs`

**Interfaces:**
- Produces: `interface IImageMatcher { IReadOnlyList<string> FindMatchingImages(string imageDirectory, string barcode); }` implemented by `class ImageMatcher : IImageMatcher`. Returns full file paths, sorted ascending by file name, for files whose name contains the barcode **as a digit-delimited match** — the barcode must be preceded and followed by a non-digit character or the string boundary, not merely a raw substring. Real production image filenames (`Demo_Data/Images/`) include barcodes like `555`, `5555`, and `55` side by side, where a naive substring match would make a shorter barcode incorrectly match a longer one's images.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/FreightSnapIngestService.Tests/Images/ImageMatcherTests.cs
using FreightSnapIngestService.Images;
using Xunit;

namespace FreightSnapIngestService.Tests.Images;

public class ImageMatcherTests : IDisposable
{
    private readonly string _dir;
    private readonly ImageMatcher _matcher = new();

    public ImageMatcherTests()
    {
        _dir = Directory.CreateTempSubdirectory("image-matcher-tests-").FullName;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    [Fact]
    public void Finds_all_files_whose_name_contains_the_barcode()
    {
        var a = Touch("012345678905_front.jpg");
        var b = Touch("012345678905_side.jpg");
        Touch("999999999999_front.jpg");

        var result = _matcher.FindMatchingImages(_dir, "012345678905");

        Assert.Equal(new[] { a, b }, result);
    }

    [Fact]
    public void Returns_results_sorted_ascending_by_file_name()
    {
        var second = Touch("012345678905_2.jpg");
        var first = Touch("012345678905_1.jpg");

        var result = _matcher.FindMatchingImages(_dir, "012345678905");

        Assert.Equal(new[] { first, second }, result);
    }

    [Fact]
    public void Returns_empty_list_when_no_images_match()
    {
        Touch("999999999999_front.jpg");

        var result = _matcher.FindMatchingImages(_dir, "012345678905");

        Assert.Empty(result);
    }

    [Fact]
    public void Does_not_match_a_shorter_barcode_that_is_a_substring_of_a_longer_one()
    {
        // Real production data has this collision: barcode "555" is a literal substring of "5555".
        Touch("5555_20260807_151343_1.jpg");

        var result = _matcher.FindMatchingImages(_dir, "555");

        Assert.Empty(result);
    }

    [Fact]
    public void Matches_a_barcode_delimited_by_non_digit_characters_even_when_a_colliding_longer_barcode_is_present()
    {
        var match = Touch("555_20260807_151435_1.jpg");
        Touch("5555_20260807_151343_1.jpg");

        var result = _matcher.FindMatchingImages(_dir, "555");

        Assert.Equal(new[] { match }, result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter ImageMatcherTests`
Expected: FAIL (compile error — `ImageMatcher` does not exist)

- [ ] **Step 3: Implement `ImageMatcher`**

```csharp
// src/FreightSnapIngestService/Images/ImageMatcher.cs
using System.Text.RegularExpressions;

namespace FreightSnapIngestService.Images;

public interface IImageMatcher
{
    IReadOnlyList<string> FindMatchingImages(string imageDirectory, string barcode);
}

public class ImageMatcher : IImageMatcher
{
    public IReadOnlyList<string> FindMatchingImages(string imageDirectory, string barcode)
    {
        if (!Directory.Exists(imageDirectory))
            return Array.Empty<string>();

        var pattern = $@"(?<!\d){Regex.Escape(barcode)}(?!\d)";

        return Directory.EnumerateFiles(imageDirectory)
            .Where(path => Regex.IsMatch(Path.GetFileName(path), pattern, RegexOptions.IgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter ImageMatcherTests`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Images tests/FreightSnapIngestService.Tests/Images
git commit -m "feat: match images to a barcode by file name"
```

---

### Task 4: Processed file naming

**Files:**
- Create: `src/FreightSnapIngestService/Naming/ScanFileNamer.cs`
- Test: `tests/FreightSnapIngestService.Tests/Naming/ScanFileNamerTests.cs`

**Interfaces:**
- Produces: `class ScanFileNamer` with:
  - `string BuildProcessedFolderName(string barcode, DateTime scanDateTime)`
  - `string BuildCsvFileName(string barcode, DateTime scanDateTime)`
  - `string BuildImageFileName(string barcode, DateTime scanDateTime, int sequenceNumber, string originalExtension)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/FreightSnapIngestService.Tests/Naming/ScanFileNamerTests.cs
using FreightSnapIngestService.Naming;
using Xunit;

namespace FreightSnapIngestService.Tests.Naming;

public class ScanFileNamerTests
{
    private readonly ScanFileNamer _namer = new();
    private static readonly DateTime ScanDateTime = new(2026, 8, 13, 9, 15, 0);

    [Fact]
    public void Builds_processed_folder_name_as_barcode_underscore_timestamp()
    {
        var name = _namer.BuildProcessedFolderName("012345678905", ScanDateTime);
        Assert.Equal("012345678905_20260813091500", name);
    }

    [Fact]
    public void Builds_csv_file_name_matching_the_folder_name_with_csv_extension()
    {
        var name = _namer.BuildCsvFileName("012345678905", ScanDateTime);
        Assert.Equal("012345678905_20260813091500.csv", name);
    }

    [Theory]
    [InlineData(1, ".jpg", "012345678905_20260813091500_1.jpg")]
    [InlineData(2, ".png", "012345678905_20260813091500_2.png")]
    public void Builds_image_file_name_with_sequence_number_and_original_extension(
        int sequenceNumber, string extension, string expected)
    {
        var name = _namer.BuildImageFileName("012345678905", ScanDateTime, sequenceNumber, extension);
        Assert.Equal(expected, name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter ScanFileNamerTests`
Expected: FAIL (compile error — `ScanFileNamer` does not exist)

- [ ] **Step 3: Implement `ScanFileNamer`**

```csharp
// src/FreightSnapIngestService/Naming/ScanFileNamer.cs
namespace FreightSnapIngestService.Naming;

public class ScanFileNamer
{
    private const string TimestampFormat = "yyyyMMddHHmmss";

    public string BuildProcessedFolderName(string barcode, DateTime scanDateTime) =>
        $"{barcode}_{scanDateTime.ToString(TimestampFormat)}";

    public string BuildCsvFileName(string barcode, DateTime scanDateTime) =>
        $"{BuildProcessedFolderName(barcode, scanDateTime)}.csv";

    public string BuildImageFileName(string barcode, DateTime scanDateTime, int sequenceNumber, string originalExtension) =>
        $"{BuildProcessedFolderName(barcode, scanDateTime)}_{sequenceNumber}{originalExtension}";
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter ScanFileNamerTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Naming tests/FreightSnapIngestService.Tests/Naming
git commit -m "feat: derive processed file and folder names from barcode and scan datetime"
```

---

### Task 5: Pending-image timeout tracking

**Files:**
- Create: `src/FreightSnapIngestService/Pending/PendingScanTracker.cs`
- Test: `tests/FreightSnapIngestService.Tests/Pending/PendingScanTrackerTests.cs`

**Interfaces:**
- Consumes: `TimeProvider` (constructor parameter).
- Produces: `class PendingScanTracker(TimeProvider timeProvider)` with:
  - `void TrackFirstSeen(string csvFilePath)` — records the current time for `csvFilePath` if not already tracked; no-ops if already tracked.
  - `bool HasTimedOut(string csvFilePath, TimeSpan timeout)` — true if `csvFilePath` is tracked and `timeProvider.GetUtcNow() - firstSeen >= timeout`. Throws `InvalidOperationException` if `csvFilePath` is not tracked.
  - `void Remove(string csvFilePath)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/FreightSnapIngestService.Tests/Pending/PendingScanTrackerTests.cs
using FreightSnapIngestService.Pending;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Pending;

public class PendingScanTrackerTests
{
    [Fact]
    public void HasTimedOut_is_false_immediately_after_first_seen()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);

        tracker.TrackFirstSeen(@"C:\csv\a.csv");

        Assert.False(tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void HasTimedOut_is_true_once_the_timeout_elapses()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);

        tracker.TrackFirstSeen(@"C:\csv\a.csv");
        time.Advance(TimeSpan.FromMinutes(31));

        Assert.True(tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void TrackFirstSeen_does_not_reset_an_already_tracked_file()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);

        tracker.TrackFirstSeen(@"C:\csv\a.csv");
        time.Advance(TimeSpan.FromMinutes(20));
        tracker.TrackFirstSeen(@"C:\csv\a.csv");
        time.Advance(TimeSpan.FromMinutes(20));

        Assert.True(tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Remove_clears_the_tracked_entry()
    {
        var time = new FakeTimeProvider();
        var tracker = new PendingScanTracker(time);
        tracker.TrackFirstSeen(@"C:\csv\a.csv");

        tracker.Remove(@"C:\csv\a.csv");

        Assert.Throws<InvalidOperationException>(
            () => tracker.HasTimedOut(@"C:\csv\a.csv", TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void HasTimedOut_throws_for_an_untracked_file()
    {
        var tracker = new PendingScanTracker(new FakeTimeProvider());

        Assert.Throws<InvalidOperationException>(
            () => tracker.HasTimedOut(@"C:\csv\unknown.csv", TimeSpan.FromMinutes(30)));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter PendingScanTrackerTests`
Expected: FAIL (compile error — `PendingScanTracker` does not exist)

- [ ] **Step 3: Implement `PendingScanTracker`**

```csharp
// src/FreightSnapIngestService/Pending/PendingScanTracker.cs
using System.Collections.Concurrent;

namespace FreightSnapIngestService.Pending;

public class PendingScanTracker(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _firstSeenUtc = new();

    public void TrackFirstSeen(string csvFilePath) =>
        _firstSeenUtc.TryAdd(csvFilePath, timeProvider.GetUtcNow());

    public bool HasTimedOut(string csvFilePath, TimeSpan timeout)
    {
        if (!_firstSeenUtc.TryGetValue(csvFilePath, out var firstSeen))
            throw new InvalidOperationException($"'{csvFilePath}' is not being tracked.");

        return timeProvider.GetUtcNow() - firstSeen >= timeout;
    }

    public void Remove(string csvFilePath) => _firstSeenUtc.TryRemove(csvFilePath, out _);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter PendingScanTrackerTests`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Pending tests/FreightSnapIngestService.Tests/Pending
git commit -m "feat: track how long a CSV has waited for matching images"
```

---

### Task 6: File-stability detection

**Files:**
- Create: `src/FreightSnapIngestService/Stability/FileStabilityTracker.cs`
- Test: `tests/FreightSnapIngestService.Tests/Stability/FileStabilityTrackerTests.cs`

**Interfaces:**
- Produces: `class FileStabilityTracker` with `bool IsStable(string filePath, long currentSize)` — false (and records the size) the first time a path/size pair not matching the last recorded size is seen; true (and forgets the path) when called again with the same size as last time.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/FreightSnapIngestService.Tests/Stability/FileStabilityTrackerTests.cs
using FreightSnapIngestService.Stability;
using Xunit;

namespace FreightSnapIngestService.Tests.Stability;

public class FileStabilityTrackerTests
{
    [Fact]
    public void First_observation_of_a_file_is_never_stable()
    {
        var tracker = new FileStabilityTracker();

        Assert.False(tracker.IsStable(@"C:\csv\a.csv", 100));
    }

    [Fact]
    public void Same_size_on_the_next_observation_is_stable()
    {
        var tracker = new FileStabilityTracker();

        tracker.IsStable(@"C:\csv\a.csv", 100);
        var result = tracker.IsStable(@"C:\csv\a.csv", 100);

        Assert.True(result);
    }

    [Fact]
    public void Changed_size_on_the_next_observation_is_not_stable()
    {
        var tracker = new FileStabilityTracker();

        tracker.IsStable(@"C:\csv\a.csv", 100);
        var result = tracker.IsStable(@"C:\csv\a.csv", 150);

        Assert.False(result);
    }

    [Fact]
    public void After_reporting_stable_the_next_observation_starts_over()
    {
        var tracker = new FileStabilityTracker();

        tracker.IsStable(@"C:\csv\a.csv", 100);
        tracker.IsStable(@"C:\csv\a.csv", 100); // stable, forgets it
        var result = tracker.IsStable(@"C:\csv\a.csv", 100);

        Assert.False(result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter FileStabilityTrackerTests`
Expected: FAIL (compile error — `FileStabilityTracker` does not exist)

- [ ] **Step 3: Implement `FileStabilityTracker`**

```csharp
// src/FreightSnapIngestService/Stability/FileStabilityTracker.cs
using System.Collections.Concurrent;

namespace FreightSnapIngestService.Stability;

public class FileStabilityTracker
{
    private readonly ConcurrentDictionary<string, long> _lastSeenSizes = new();

    public bool IsStable(string filePath, long currentSize)
    {
        if (_lastSeenSizes.TryGetValue(filePath, out var lastSize) && lastSize == currentSize)
        {
            _lastSeenSizes.TryRemove(filePath, out _);
            return true;
        }

        _lastSeenSizes[filePath] = currentSize;
        return false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter FileStabilityTrackerTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Stability tests/FreightSnapIngestService.Tests/Stability
git commit -m "feat: detect when a CSV file has finished being written"
```

---

### Task 7: SQL Server schema and repository

**Files:**
- Create: `db/schema.sql`
- Create: `src/FreightSnapIngestService/Db/SchemaSql.cs`
- Create: `src/FreightSnapIngestService/Db/FreightScanRepository.cs`
- Create: `tests/FreightSnapIngestService.Tests/Db/SqlServerTestFixture.cs`
- Test: `tests/FreightSnapIngestService.Tests/Db/FreightScanRepositoryTests.cs`

**Interfaces:**
- Consumes: `ScanRecord` (Task 2).
- Produces: `interface IFreightScanRepository`:
  - `Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)` — inserts one `FreightScans` row and one `FreightScanImages` row per image path in a single transaction; returns the new scan `Id`.
  - `Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct)` — deletes `FreightScanImages` rows and nulls `FreightScans.CsvPath` for paths starting with `folderPathPrefix`, in a single transaction.
  - implemented by `class FreightScanRepository(string connectionString) : IFreightScanRepository`.

This task requires a local SQL Server instance reachable via LocalDB. If `(localdb)\MSSQLLocalDB` is not available in the dev environment, install it via the "SQL Server Express LocalDB" component of the SQL Server installer, or point `LocalDbConnectionString` in the test fixture at any reachable disposable SQL Server/Express instance.

- [ ] **Step 1: Write the schema as a shared constant and a deployable script**

```csharp
// src/FreightSnapIngestService/Db/SchemaSql.cs
namespace FreightSnapIngestService.Db;

public static class SchemaSql
{
    public const string CreateTables = """
        IF OBJECT_ID('dbo.FreightScanImages', 'U') IS NULL AND OBJECT_ID('dbo.FreightScans', 'U') IS NULL
        BEGIN
            CREATE TABLE dbo.FreightScans (
                Id           BIGINT IDENTITY PRIMARY KEY,
                Barcode      VARCHAR(64)    NOT NULL,
                ScanDateTime DATETIME2      NOT NULL,
                Length       DECIMAL(10,3)  NULL,
                Width        DECIMAL(10,3)  NULL,
                Height       DECIMAL(10,3)  NULL,
                Volume       DECIMAL(10,3)  NULL,
                Weight       DECIMAL(10,3)  NULL,
                CsvPath      NVARCHAR(400)  NULL,
                ProcessedAt  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
            );

            CREATE INDEX IX_FreightScans_Barcode ON dbo.FreightScans(Barcode);

            CREATE TABLE dbo.FreightScanImages (
                Id            BIGINT IDENTITY PRIMARY KEY,
                FreightScanId BIGINT         NOT NULL
                    REFERENCES dbo.FreightScans(Id),
                ImagePath     NVARCHAR(400)  NOT NULL
            );

            CREATE INDEX IX_FreightScanImages_FreightScanId
                ON dbo.FreightScanImages(FreightScanId);
        END
        """;
}
```

```sql
-- db/schema.sql
-- Deployable copy of FreightSnapIngestService.Db.SchemaSql.CreateTables.
-- Keep in sync manually if the schema changes; changes are rare.
IF OBJECT_ID('dbo.FreightScanImages', 'U') IS NULL AND OBJECT_ID('dbo.FreightScans', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.FreightScans (
        Id           BIGINT IDENTITY PRIMARY KEY,
        Barcode      VARCHAR(64)    NOT NULL,
        ScanDateTime DATETIME2      NOT NULL,
        Length       DECIMAL(10,3)  NULL,
        Width        DECIMAL(10,3)  NULL,
        Height       DECIMAL(10,3)  NULL,
        Volume       DECIMAL(10,3)  NULL,
        Weight       DECIMAL(10,3)  NULL,
        CsvPath      NVARCHAR(400)  NULL,
        ProcessedAt  DATETIME2      NOT NULL DEFAULT SYSUTCDATETIME()
    );

    CREATE INDEX IX_FreightScans_Barcode ON dbo.FreightScans(Barcode);

    CREATE TABLE dbo.FreightScanImages (
        Id            BIGINT IDENTITY PRIMARY KEY,
        FreightScanId BIGINT         NOT NULL
            REFERENCES dbo.FreightScans(Id),
        ImagePath     NVARCHAR(400)  NOT NULL
    );

    CREATE INDEX IX_FreightScanImages_FreightScanId
        ON dbo.FreightScanImages(FreightScanId);
END
```

- [ ] **Step 2: Write the test fixture that creates and tears down a disposable test database**

```csharp
// tests/FreightSnapIngestService.Tests/Db/SqlServerTestFixture.cs
using FreightSnapIngestService.Db;
using Microsoft.Data.SqlClient;

namespace FreightSnapIngestService.Tests.Db;

public class SqlServerTestFixture : IAsyncLifetime
{
    private const string MasterConnectionString =
        @"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True;";

    public string DatabaseName { get; } = $"FreightSnapIngestTest_{Guid.NewGuid():N}";

    public string ConnectionString =>
        $@"Server=(localdb)\MSSQLLocalDB;Database={DatabaseName};Trusted_Connection=True;TrustServerCertificate=True;";

    public async Task InitializeAsync()
    {
        await using (var master = new SqlConnection(MasterConnectionString))
        {
            await master.OpenAsync();
            await using var create = master.CreateCommand();
            create.CommandText = $"CREATE DATABASE [{DatabaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        await using var schema = db.CreateCommand();
        schema.CommandText = SchemaSql.CreateTables;
        await schema.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();
        await using var drop = master.CreateCommand();
        drop.CommandText =
            $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];";
        await drop.ExecuteNonQueryAsync();
    }
}
```

- [ ] **Step 3: Write the failing repository tests**

```csharp
// tests/FreightSnapIngestService.Tests/Db/FreightScanRepositoryTests.cs
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace FreightSnapIngestService.Tests.Db;

public class FreightScanRepositoryTests : IClassFixture<SqlServerTestFixture>
{
    private readonly SqlServerTestFixture _fixture;

    public FreightScanRepositoryTests(SqlServerTestFixture fixture) => _fixture = fixture;

    private static readonly ScanRecord Scan = new(
        "012345678905", new DateTime(2026, 8, 13, 9, 15, 0), 12.5m, 8.0m, 6.25m, 625.0m, 14.2m);

    [Fact]
    public async Task InsertScanAsync_writes_the_scan_and_its_images()
    {
        var repository = new FreightScanRepository(_fixture.ConnectionString);
        var csvPath = @"C:\FreightSnap\processed\012345678905_20260813091500\012345678905_20260813091500.csv";
        var imagePaths = new[]
        {
            @"C:\FreightSnap\processed\012345678905_20260813091500\012345678905_20260813091500_1.jpg",
            @"C:\FreightSnap\processed\012345678905_20260813091500\012345678905_20260813091500_2.jpg",
        };

        var scanId = await repository.InsertScanAsync(Scan, csvPath, imagePaths, CancellationToken.None);

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText = "SELECT Barcode, CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        scanCmd.Parameters.AddWithValue("@Id", scanId);
        await using var reader = await scanCmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("012345678905", reader.GetString(0));
        Assert.Equal(csvPath, reader.GetString(1));
        await reader.CloseAsync();

        await using var imageCmd = conn.CreateCommand();
        imageCmd.CommandText = "SELECT COUNT(*) FROM dbo.FreightScanImages WHERE FreightScanId = @Id";
        imageCmd.Parameters.AddWithValue("@Id", scanId);
        var imageCount = (int)(await imageCmd.ExecuteScalarAsync())!;
        Assert.Equal(2, imageCount);
    }

    [Fact]
    public async Task ClearPathsUnderFolderAsync_nulls_csv_path_and_deletes_image_rows_under_the_folder()
    {
        var repository = new FreightScanRepository(_fixture.ConnectionString);
        var folder = @"C:\FreightSnap\processed\999999999999_20260813120000";
        var csvPath = $@"{folder}\999999999999_20260813120000.csv";
        var imagePath = $@"{folder}\999999999999_20260813120000_1.jpg";
        var otherScan = Scan with { Barcode = "999999999999" };
        var scanId = await repository.InsertScanAsync(otherScan, csvPath, new[] { imagePath }, CancellationToken.None);

        await repository.ClearPathsUnderFolderAsync(folder, CancellationToken.None);

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText = "SELECT CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        scanCmd.Parameters.AddWithValue("@Id", scanId);
        var csvPathAfter = await scanCmd.ExecuteScalarAsync();
        Assert.Equal(DBNull.Value, csvPathAfter);

        await using var imageCmd = conn.CreateCommand();
        imageCmd.CommandText = "SELECT COUNT(*) FROM dbo.FreightScanImages WHERE FreightScanId = @Id";
        imageCmd.Parameters.AddWithValue("@Id", scanId);
        var imageCount = (int)(await imageCmd.ExecuteScalarAsync())!;
        Assert.Equal(0, imageCount);
    }

    [Fact]
    public async Task ClearPathsUnderFolderAsync_does_not_clear_a_different_scan_whose_path_only_matches_via_the_underscore_wildcard()
    {
        // SQL LIKE treats '_' as "match any single character." The real folder-naming
        // convention is "{barcode}_{timestamp}", so every prefix contains underscores —
        // ClearPathsUnderFolderAsync must escape them, or a prefix like ".../AAA_1" would
        // also match an unrelated path like ".../AAAX1" purely via wildcard substitution.
        var repository = new FreightScanRepository(_fixture.ConnectionString);
        var targetFolder = @"C:\FreightSnap\processed\AAA_1";
        var targetCsvPath = $@"{targetFolder}\AAA_1.csv";
        var targetScan = Scan with { Barcode = "AAA1TARGET" };
        var targetScanId = await repository.InsertScanAsync(targetScan, targetCsvPath, Array.Empty<string>(), CancellationToken.None);

        var otherCsvPath = @"C:\FreightSnap\processed\AAAX1\AAAX1.csv"; // same length, '_' replaced by 'X'
        var otherScan = Scan with { Barcode = "AAAXOTHER" };
        var otherScanId = await repository.InsertScanAsync(otherScan, otherCsvPath, Array.Empty<string>(), CancellationToken.None);

        await repository.ClearPathsUnderFolderAsync(targetFolder, CancellationToken.None);

        await using var conn = new SqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        await using var targetCmd = conn.CreateCommand();
        targetCmd.CommandText = "SELECT CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        targetCmd.Parameters.AddWithValue("@Id", targetScanId);
        Assert.Equal(DBNull.Value, await targetCmd.ExecuteScalarAsync());

        await using var otherCmd = conn.CreateCommand();
        otherCmd.CommandText = "SELECT CsvPath FROM dbo.FreightScans WHERE Id = @Id";
        otherCmd.Parameters.AddWithValue("@Id", otherScanId);
        Assert.Equal(otherCsvPath, await otherCmd.ExecuteScalarAsync());
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter FreightScanRepositoryTests`
Expected: FAIL (compile error — `FreightScanRepository` does not exist)

- [ ] **Step 5: Implement `FreightScanRepository`**

```csharp
// src/FreightSnapIngestService/Db/FreightScanRepository.cs
using FreightSnapIngestService.Models;
using Microsoft.Data.SqlClient;

namespace FreightSnapIngestService.Db;

public interface IFreightScanRepository
{
    Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct);
    Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct);
}

public class FreightScanRepository(string connectionString) : IFreightScanRepository
{
    public async Task<long> InsertScanAsync(
        ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await using var insertScan = conn.CreateCommand();
        insertScan.Transaction = transaction;
        insertScan.CommandText = """
            INSERT INTO dbo.FreightScans (Barcode, ScanDateTime, Length, Width, Height, Volume, Weight, CsvPath)
            OUTPUT INSERTED.Id
            VALUES (@Barcode, @ScanDateTime, @Length, @Width, @Height, @Volume, @Weight, @CsvPath);
            """;
        insertScan.Parameters.AddWithValue("@Barcode", scan.Barcode);
        insertScan.Parameters.AddWithValue("@ScanDateTime", scan.ScanDateTime);
        insertScan.Parameters.AddWithValue("@Length", (object?)scan.Length ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Width", (object?)scan.Width ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Height", (object?)scan.Height ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Volume", (object?)scan.Volume ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@Weight", (object?)scan.Weight ?? DBNull.Value);
        insertScan.Parameters.AddWithValue("@CsvPath", (object?)csvPath ?? DBNull.Value);

        var scanId = (long)(await insertScan.ExecuteScalarAsync(ct))!;

        foreach (var imagePath in imagePaths)
        {
            await using var insertImage = conn.CreateCommand();
            insertImage.Transaction = transaction;
            insertImage.CommandText = """
                INSERT INTO dbo.FreightScanImages (FreightScanId, ImagePath)
                VALUES (@FreightScanId, @ImagePath);
                """;
            insertImage.Parameters.AddWithValue("@FreightScanId", scanId);
            insertImage.Parameters.AddWithValue("@ImagePath", imagePath);
            await insertImage.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return scanId;
    }

    public async Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        // SQL LIKE treats '_', '%', and '[' as wildcards. The folder-naming convention is
        // "{barcode}_{timestamp}", so every real prefix contains underscores that must be
        // escaped — otherwise this could match and clear an unrelated scan's paths.
        var escapedPrefix = EscapeLikeWildcards(folderPathPrefix);

        await using var deleteImages = conn.CreateCommand();
        deleteImages.Transaction = transaction;
        deleteImages.CommandText = """
            DELETE FROM dbo.FreightScanImages
            WHERE ImagePath LIKE @Prefix + '%' ESCAPE '\';
            """;
        deleteImages.Parameters.AddWithValue("@Prefix", escapedPrefix);
        await deleteImages.ExecuteNonQueryAsync(ct);

        await using var clearCsvPath = conn.CreateCommand();
        clearCsvPath.Transaction = transaction;
        clearCsvPath.CommandText = """
            UPDATE dbo.FreightScans
            SET CsvPath = NULL
            WHERE CsvPath LIKE @Prefix + '%' ESCAPE '\';
            """;
        clearCsvPath.Parameters.AddWithValue("@Prefix", escapedPrefix);
        await clearCsvPath.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
    }

    private static string EscapeLikeWildcards(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter FreightScanRepositoryTests`
Expected: PASS (3 tests) — requires `(localdb)\MSSQLLocalDB` to be available

- [ ] **Step 7: Commit**

```bash
git add db src/FreightSnapIngestService/Db tests/FreightSnapIngestService.Tests/Db
git commit -m "feat: add SQL Server schema and FreightScanRepository"
```

---

### Task 8: Ingest pipeline orchestration

**Files:**
- Create: `src/FreightSnapIngestService/Ingestion/IngestPipeline.cs`
- Test: `tests/FreightSnapIngestService.Tests/Ingestion/IngestPipelineTests.cs`

**Interfaces:**
- Consumes: `FreightIngestOptions` (Task 1), `ICsvScanParser` (Task 2), `IImageMatcher` (Task 3), `ScanFileNamer` (Task 4), `PendingScanTracker` (Task 5), `FileStabilityTracker` (Task 6), `IFreightScanRepository` (Task 7), `ILogger<IngestPipeline>`.
- Produces: `class IngestPipeline(FreightIngestOptions options, ICsvScanParser csvParser, IImageMatcher imageMatcher, ScanFileNamer fileNamer, PendingScanTracker pendingTracker, FileStabilityTracker stabilityTracker, IFreightScanRepository repository, ILogger<IngestPipeline> logger)` with `Task ProcessOnceAsync(CancellationToken ct)` — call this once per ingest poll tick. Logs every finalize, skip (still pending), pending-timeout finalize, parse error, and DB error, each with the barcode and/or file name involved.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/FreightSnapIngestService.Tests/Ingestion/IngestPipelineTests.cs
using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Ingestion;
using FreightSnapIngestService.Models;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Stability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Ingestion;

public class FakeFreightScanRepository : IFreightScanRepository
{
    public List<(ScanRecord Scan, string? CsvPath, IReadOnlyList<string> ImagePaths)> Inserted { get; } = new();
    public bool ThrowOnInsert { get; set; }

    public Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
    {
        if (ThrowOnInsert) throw new InvalidOperationException("simulated DB failure");
        Inserted.Add((scan, csvPath, imagePaths));
        return Task.FromResult((long)Inserted.Count);
    }

    public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct) => Task.CompletedTask;
}

public class IngestPipelineTests : IDisposable
{
    private readonly string _root;
    private readonly FreightIngestOptions _options;
    private readonly FakeFreightScanRepository _repository = new();
    private readonly FakeTimeProvider _time = new();

    public IngestPipelineTests()
    {
        _root = Directory.CreateTempSubdirectory("ingest-pipeline-tests-").FullName;
        _options = new FreightIngestOptions
        {
            CsvDirectory = Path.Combine(_root, "csv"),
            ImageDirectory = Path.Combine(_root, "images"),
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            PendingTimeoutMinutes = 30,
        };
        Directory.CreateDirectory(_options.CsvDirectory);
        Directory.CreateDirectory(_options.ImageDirectory);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private IngestPipeline CreatePipeline() => new(
        _options,
        new CsvScanParser(),
        new ImageMatcher(),
        new ScanFileNamer(),
        new PendingScanTracker(_time),
        new FileStabilityTracker(),
        _repository,
        NullLogger<IngestPipeline>.Instance);

    private const string ValidCsv =
        "012345678905,20260813091500,12.5,8.0,6.25,625.0,14.2\n";

    private string WriteCsv(string fileName, string content)
    {
        var path = Path.Combine(_options.CsvDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteImage(string fileName)
    {
        var path = Path.Combine(_options.ImageDirectory, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public async Task Csv_is_not_processed_on_the_first_poll_it_appears_stable_check()
    {
        WriteCsv("scan1.csv", ValidCsv);
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
    }

    [Fact]
    public async Task Finalizes_a_stable_csv_with_matching_images()
    {
        WriteCsv("scan1.csv", ValidCsv);
        WriteImage("012345678905_1.jpg");
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None); // first poll: marks stable-check baseline
        await pipeline.ProcessOnceAsync(CancellationToken.None); // second poll: size unchanged, finalizes

        Assert.Single(_repository.Inserted);
        Assert.Equal("012345678905", _repository.Inserted[0].Scan.Barcode);
        Assert.Single(_repository.Inserted[0].ImagePaths);

        var processedFolder = Path.Combine(_options.ProcessedDirectory, "012345678905_20260813091500");
        Assert.True(File.Exists(Path.Combine(processedFolder, "012345678905_20260813091500.csv")));
        Assert.True(File.Exists(Path.Combine(processedFolder, "012345678905_20260813091500_1.jpg")));
        Assert.False(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
        Assert.False(File.Exists(Path.Combine(_options.ImageDirectory, "012345678905_1.jpg")));
    }

    [Fact]
    public async Task Leaves_a_stable_csv_pending_when_no_images_match_yet_and_timeout_has_not_elapsed()
    {
        WriteCsv("scan1.csv", ValidCsv);
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
    }

    [Fact]
    public async Task Finalizes_without_images_once_the_pending_timeout_elapses()
    {
        WriteCsv("scan1.csv", ValidCsv);
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None); // now pending, no images
        _time.Advance(TimeSpan.FromMinutes(31));
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Single(_repository.Inserted);
        Assert.Empty(_repository.Inserted[0].ImagePaths);
        var processedFolder = Path.Combine(_options.ProcessedDirectory, "012345678905_20260813091500");
        Assert.True(File.Exists(Path.Combine(processedFolder, "012345678905_20260813091500.csv")));
    }

    [Fact]
    public async Task Moves_a_malformed_csv_to_the_error_directory_immediately()
    {
        WriteCsv("bad.csv", "only,two\n");
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.ErrorDirectory, "bad.csv")));
        Assert.False(File.Exists(Path.Combine(_options.CsvDirectory, "bad.csv")));
    }

    [Fact]
    public async Task Restores_the_original_files_when_the_database_insert_fails()
    {
        WriteCsv("scan1.csv", ValidCsv);
        WriteImage("012345678905_1.jpg");
        _repository.ThrowOnInsert = true;
        var pipeline = CreatePipeline();

        await pipeline.ProcessOnceAsync(CancellationToken.None);
        await pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.Empty(_repository.Inserted);
        Assert.True(File.Exists(Path.Combine(_options.CsvDirectory, "scan1.csv")));
        Assert.True(File.Exists(Path.Combine(_options.ImageDirectory, "012345678905_1.jpg")));
        Assert.False(Directory.Exists(_options.ProcessedDirectory) &&
            Directory.EnumerateFileSystemEntries(_options.ProcessedDirectory).Any());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter IngestPipelineTests`
Expected: FAIL (compile error — `IngestPipeline` does not exist)

- [ ] **Step 3: Implement `IngestPipeline`**

```csharp
// src/FreightSnapIngestService/Ingestion/IngestPipeline.cs
using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Stability;

namespace FreightSnapIngestService.Ingestion;

public class IngestPipeline(
    FreightIngestOptions options,
    ICsvScanParser csvParser,
    IImageMatcher imageMatcher,
    ScanFileNamer fileNamer,
    PendingScanTracker pendingTracker,
    FileStabilityTracker stabilityTracker,
    IFreightScanRepository repository,
    ILogger<IngestPipeline> logger)
{
    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        if (!Directory.Exists(options.CsvDirectory))
            return;

        foreach (var csvPath in Directory.EnumerateFiles(options.CsvDirectory, "*.csv"))
        {
            try
            {
                await ProcessCsvAsync(csvPath, ct);
            }
            catch (Exception ex)
            {
                // A single bad file must not stop the rest of the batch; it is retried
                // on the next poll cycle since it was never moved out of CsvDirectory.
                logger.LogError(ex, "Failed to process {CsvPath}; it will be retried next poll cycle.", csvPath);
            }
        }
    }

    private async Task ProcessCsvAsync(string csvPath, CancellationToken ct)
    {
        var currentSize = new FileInfo(csvPath).Length;
        if (!stabilityTracker.IsStable(csvPath, currentSize))
            return;

        Models.ScanRecord scan;
        try
        {
            scan = csvParser.Parse(await File.ReadAllTextAsync(csvPath, ct));
        }
        catch (CsvParseException ex)
        {
            logger.LogWarning(ex, "{CsvPath} failed to parse and is being moved to the error directory.", csvPath);
            Directory.CreateDirectory(options.ErrorDirectory);
            File.Move(csvPath, Path.Combine(options.ErrorDirectory, Path.GetFileName(csvPath)), overwrite: true);
            return;
        }

        var matchedImages = imageMatcher.FindMatchingImages(options.ImageDirectory, scan.Barcode);

        if (matchedImages.Count == 0)
        {
            pendingTracker.TrackFirstSeen(csvPath);
            if (!pendingTracker.HasTimedOut(csvPath, TimeSpan.FromMinutes(options.PendingTimeoutMinutes)))
            {
                logger.LogInformation(
                    "Barcode {Barcode} ({CsvPath}) has no matching images yet; leaving pending.",
                    scan.Barcode, csvPath);
                return;
            }

            logger.LogWarning(
                "Barcode {Barcode} ({CsvPath}) timed out waiting for images after {TimeoutMinutes} minutes; finalizing without images.",
                scan.Barcode, csvPath, options.PendingTimeoutMinutes);
        }

        await FinalizeAsync(csvPath, scan, matchedImages, ct);
        pendingTracker.Remove(csvPath);
        logger.LogInformation(
            "Finalized barcode {Barcode} ({CsvPath}) with {ImageCount} image(s).",
            scan.Barcode, csvPath, matchedImages.Count);
    }

    private async Task FinalizeAsync(string csvPath, Models.ScanRecord scan, IReadOnlyList<string> matchedImages, CancellationToken ct)
    {
        var folderName = fileNamer.BuildProcessedFolderName(scan.Barcode, scan.ScanDateTime);
        var destinationFolder = Path.Combine(options.ProcessedDirectory, folderName);
        Directory.CreateDirectory(destinationFolder);

        var destinationCsvPath = Path.Combine(destinationFolder, fileNamer.BuildCsvFileName(scan.Barcode, scan.ScanDateTime));
        File.Move(csvPath, destinationCsvPath);

        var destinationImagePaths = new List<string>();
        var movedSourceImagePaths = new List<string>();
        try
        {
            for (var i = 0; i < matchedImages.Count; i++)
            {
                var sourceImagePath = matchedImages[i];
                var extension = Path.GetExtension(sourceImagePath);
                var destinationImagePath = Path.Combine(
                    destinationFolder,
                    fileNamer.BuildImageFileName(scan.Barcode, scan.ScanDateTime, i + 1, extension));

                File.Move(sourceImagePath, destinationImagePath);
                movedSourceImagePaths.Add(sourceImagePath);
                destinationImagePaths.Add(destinationImagePath);
            }

            await repository.InsertScanAsync(scan, destinationCsvPath, destinationImagePaths, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Database insert failed for barcode {Barcode} ({CsvPath}); reverting file moves for retry.",
                scan.Barcode, csvPath);

            File.Move(destinationCsvPath, csvPath);
            for (var i = 0; i < movedSourceImagePaths.Count; i++)
                File.Move(destinationImagePaths[i], movedSourceImagePaths[i]);

            if (!Directory.EnumerateFileSystemEntries(destinationFolder).Any())
                Directory.Delete(destinationFolder);

            throw;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter IngestPipelineTests`
Expected: PASS (6 tests)

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Ingestion tests/FreightSnapIngestService.Tests/Ingestion
git commit -m "feat: orchestrate CSV parsing, image matching, and finalization into IngestPipeline"
```

---

### Task 9: Ingest background service

**Files:**
- Create: `src/FreightSnapIngestService/Ingestion/IngestWorker.cs`
- Test: `tests/FreightSnapIngestService.Tests/Ingestion/IngestWorkerTests.cs`

**Interfaces:**
- Consumes: `IngestPipeline` (Task 8), `FreightIngestOptions` (Task 1), `TimeProvider`.
- Produces: `class IngestWorker(IngestPipeline pipeline, Microsoft.Extensions.Options.IOptions<FreightIngestOptions> options, TimeProvider timeProvider, ILogger<IngestWorker> logger) : BackgroundService`. Runs `pipeline.ProcessOnceAsync` once per `PollIntervalSeconds` using a `PeriodicTimer`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/FreightSnapIngestService.Tests/Ingestion/IngestWorkerTests.cs
using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Ingestion;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Stability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Ingestion;

public class IngestWorkerTests : IDisposable
{
    private readonly string _root;

    public IngestWorkerTests() => _root = Directory.CreateTempSubdirectory("ingest-worker-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Polls_the_pipeline_repeatedly_at_the_configured_interval()
    {
        var freightOptions = new FreightIngestOptions
        {
            CsvDirectory = Path.Combine(_root, "csv"),
            ImageDirectory = Path.Combine(_root, "images"),
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            PollIntervalSeconds = 10,
            PendingTimeoutMinutes = 30,
        };
        Directory.CreateDirectory(freightOptions.CsvDirectory);
        Directory.CreateDirectory(freightOptions.ImageDirectory);

        var time = new FakeTimeProvider();
        var pipeline = new IngestPipeline(
            freightOptions, new CsvScanParser(), new ImageMatcher(), new ScanFileNamer(),
            new PendingScanTracker(time), new FileStabilityTracker(), new NoOpFreightScanRepository(),
            NullLogger<IngestPipeline>.Instance);

        var worker = new IngestWorker(pipeline, Options.Create(freightOptions), time, NullLogger<IngestWorker>.Instance);

        using var cts = new CancellationTokenSource();
        var runTask = worker.StartAsync(cts.Token);
        await runTask;

        // Advance past three poll intervals; each tick should complete without throwing
        // even with empty directories, proving the loop is alive and self-driving.
        for (var i = 0; i < 3; i++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(50); // let the background loop observe the advanced time
        }

        await worker.StopAsync(CancellationToken.None);
    }

    private class NoOpFreightScanRepository : IFreightScanRepository
    {
        public Task<long> InsertScanAsync(Models.ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
            => Task.FromResult(1L);
        public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct) => Task.CompletedTask;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter IngestWorkerTests`
Expected: FAIL (compile error — `IngestWorker` does not exist)

- [ ] **Step 3: Implement `IngestWorker`**

```csharp
// src/FreightSnapIngestService/Ingestion/IngestWorker.cs
using FreightSnapIngestService.Options;
using Microsoft.Extensions.Options;

namespace FreightSnapIngestService.Ingestion;

public class IngestWorker(
    IngestPipeline pipeline,
    IOptions<FreightIngestOptions> options,
    TimeProvider timeProvider,
    ILogger<IngestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollIntervalSeconds), timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await pipeline.ProcessOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Ingest poll cycle failed.");
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter IngestWorkerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Ingestion/IngestWorker.cs tests/FreightSnapIngestService.Tests/Ingestion/IngestWorkerTests.cs
git commit -m "feat: drive IngestPipeline on a configurable poll interval"
```

---

### Task 10: Retention cleanup logic

**Files:**
- Create: `src/FreightSnapIngestService/Retention/RetentionCleaner.cs`
- Test: `tests/FreightSnapIngestService.Tests/Retention/RetentionCleanerTests.cs`

**Interfaces:**
- Consumes: `FreightIngestOptions` (Task 1), `IFreightScanRepository` (Task 7), `TimeProvider`.
- Produces: `class RetentionCleaner(FreightIngestOptions options, IFreightScanRepository repository, TimeProvider timeProvider)` with `Task RunOnceAsync(CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/FreightSnapIngestService.Tests/Retention/RetentionCleanerTests.cs
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Models;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Retention;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Retention;

public class FakeFreightScanRepository : IFreightScanRepository
{
    public List<string> ClearedFolderPrefixes { get; } = new();

    public Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
        => Task.FromResult(1L);

    public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct)
    {
        ClearedFolderPrefixes.Add(folderPathPrefix);
        return Task.CompletedTask;
    }
}

public class RetentionCleanerTests : IDisposable
{
    private readonly string _root;
    private readonly FreightIngestOptions _options;
    private readonly FakeFreightScanRepository _repository = new();
    private readonly FakeTimeProvider _time = new();

    public RetentionCleanerTests()
    {
        _root = Directory.CreateTempSubdirectory("retention-cleaner-tests-").FullName;
        _options = new FreightIngestOptions
        {
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            RetentionDays = 30,
        };
        Directory.CreateDirectory(_options.ProcessedDirectory);
        Directory.CreateDirectory(_options.ErrorDirectory);
        _time.SetUtcNow(new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreateProcessedFolder(string name, DateTime lastWriteTimeUtc)
    {
        var folder = Path.Combine(_options.ProcessedDirectory, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"{name}.csv"), "x");
        Directory.SetLastWriteTimeUtc(folder, lastWriteTimeUtc);
        return folder;
    }

    [Fact]
    public async Task Deletes_processed_folders_older_than_the_retention_period_and_clears_db_paths()
    {
        var oldFolder = CreateProcessedFolder("012345678905_20260601090000", new DateTime(2026, 6, 1));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(oldFolder));
        Assert.Contains(oldFolder, _repository.ClearedFolderPrefixes);
    }

    [Fact]
    public async Task Keeps_processed_folders_within_the_retention_period()
    {
        var recentFolder = CreateProcessedFolder("012345678905_20260812090000", new DateTime(2026, 8, 12));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.True(Directory.Exists(recentFolder));
        Assert.DoesNotContain(recentFolder, _repository.ClearedFolderPrefixes);
    }

    [Fact]
    public async Task Deletes_expired_files_directly_under_the_error_directory()
    {
        var errorFile = Path.Combine(_options.ErrorDirectory, "bad.csv");
        File.WriteAllText(errorFile, "x");
        File.SetLastWriteTimeUtc(errorFile, new DateTime(2026, 6, 1));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(File.Exists(errorFile));
    }

    [Fact]
    public async Task Keeps_recent_files_under_the_error_directory()
    {
        var errorFile = Path.Combine(_options.ErrorDirectory, "bad.csv");
        File.WriteAllText(errorFile, "x");
        File.SetLastWriteTimeUtc(errorFile, new DateTime(2026, 8, 12));
        var cleaner = new RetentionCleaner(_options, _repository, _time);

        await cleaner.RunOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(errorFile));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter RetentionCleanerTests`
Expected: FAIL (compile error — `RetentionCleaner` does not exist)

- [ ] **Step 3: Implement `RetentionCleaner`**

```csharp
// src/FreightSnapIngestService/Retention/RetentionCleaner.cs
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Options;

namespace FreightSnapIngestService.Retention;

public class RetentionCleaner(
    FreightIngestOptions options,
    IFreightScanRepository repository,
    TimeProvider timeProvider)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(options.RetentionDays);

        if (Directory.Exists(options.ProcessedDirectory))
        {
            foreach (var folder in Directory.EnumerateDirectories(options.ProcessedDirectory))
            {
                if (Directory.GetLastWriteTimeUtc(folder) >= cutoff)
                    continue;

                Directory.Delete(folder, recursive: true);
                await repository.ClearPathsUnderFolderAsync(folder, ct);
            }
        }

        if (Directory.Exists(options.ErrorDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(options.ErrorDirectory))
            {
                if (File.GetLastWriteTimeUtc(file) >= cutoff)
                    continue;

                File.Delete(file);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter RetentionCleanerTests`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Retention/RetentionCleaner.cs tests/FreightSnapIngestService.Tests/Retention/RetentionCleanerTests.cs
git commit -m "feat: delete expired processed/error output and clear their DB path references"
```

---

### Task 11: Retention background service

**Files:**
- Create: `src/FreightSnapIngestService/Retention/RetentionWorker.cs`
- Test: `tests/FreightSnapIngestService.Tests/Retention/RetentionWorkerTests.cs`

**Interfaces:**
- Consumes: `RetentionCleaner` (Task 10), `TimeProvider`.
- Produces: `class RetentionWorker(RetentionCleaner cleaner, TimeProvider timeProvider, ILogger<RetentionWorker> logger) : BackgroundService`. Runs `cleaner.RunOnceAsync` once per hour using a `PeriodicTimer`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/FreightSnapIngestService.Tests/Retention/RetentionWorkerTests.cs
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Models;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Retention;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.Retention;

public class RetentionWorkerTests : IDisposable
{
    private readonly string _root;

    public RetentionWorkerTests() => _root = Directory.CreateTempSubdirectory("retention-worker-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private class NoOpFreightScanRepository : IFreightScanRepository
    {
        public Task<long> InsertScanAsync(ScanRecord scan, string? csvPath, IReadOnlyList<string> imagePaths, CancellationToken ct)
            => Task.FromResult(1L);
        public Task ClearPathsUnderFolderAsync(string folderPathPrefix, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Runs_the_cleaner_once_per_hour()
    {
        var options = new FreightIngestOptions
        {
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            RetentionDays = 30,
        };
        Directory.CreateDirectory(options.ProcessedDirectory);
        Directory.CreateDirectory(options.ErrorDirectory);

        var time = new FakeTimeProvider();
        var cleaner = new RetentionCleaner(options, new NoOpFreightScanRepository(), time);
        var worker = new RetentionWorker(cleaner, time, NullLogger<RetentionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        await worker.StopAsync(CancellationToken.None);

        // No exception means the hourly tick executed RunOnceAsync against real,
        // empty directories without error.
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter RetentionWorkerTests`
Expected: FAIL (compile error — `RetentionWorker` does not exist)

- [ ] **Step 3: Implement `RetentionWorker`**

```csharp
// src/FreightSnapIngestService/Retention/RetentionWorker.cs
namespace FreightSnapIngestService.Retention;

public class RetentionWorker(
    RetentionCleaner cleaner,
    TimeProvider timeProvider,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await cleaner.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Retention cleanup cycle failed.");
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter RetentionWorkerTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/FreightSnapIngestService/Retention/RetentionWorker.cs tests/FreightSnapIngestService.Tests/Retention/RetentionWorkerTests.cs
git commit -m "feat: run RetentionCleaner hourly"
```

---

### Task 12: Wire up the host and Windows Service packaging

**Files:**
- Modify: `src/FreightSnapIngestService/Program.cs`
- Modify: `src/FreightSnapIngestService/FreightSnapIngestService.csproj`
- Create: `README.md`

**Interfaces:**
- Consumes: every class produced in Tasks 1–11.
- Produces: a runnable/installable Windows Service; no new public interfaces.

- [ ] **Step 1: Register all services in `Program.cs`**

```csharp
// src/FreightSnapIngestService/Program.cs
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
    sp.GetRequiredService<TimeProvider>()));

builder.Services.AddHostedService<IngestWorker>();
builder.Services.AddHostedService<RetentionWorker>();

var host = builder.Build();
host.Run();
```

- [ ] **Step 2: Enable Windows Service self-contained publishing in the csproj**

Add inside the existing `<PropertyGroup>` of `src/FreightSnapIngestService/FreightSnapIngestService.csproj`:

```xml
<UseWindowsService>true</UseWindowsService>
```

- [ ] **Step 3: Build the full solution**

Run: `dotnet build FreightSnapIngestService.sln`
Expected: Build succeeded

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test FreightSnapIngestService.sln`
Expected: PASS — all tests from Tasks 1–11 (requires `(localdb)\MSSQLLocalDB` for the Task 7 repository tests)

- [ ] **Step 5: Write the README with deployment steps**

```markdown
<!-- README.md -->
# Freight Snap Ingest Service

Windows Service that ingests CSV scan records and matched barcode images
into SQL Server. See `docs/superpowers/specs/2026-08-13-freight-snap-ingest-service-design.md`
for the full design.

## Configure

Edit `src/FreightSnapIngestService/appsettings.json` (or an environment-specific
override) with real directory paths and connection string before publishing.

## Deploy the database schema

Run `db/schema.sql` against the target SQL Server database once, before starting
the service for the first time.

## Publish and install as a Windows Service

```powershell
dotnet publish src/FreightSnapIngestService -c Release -r win-x64 --self-contained false -o publish

New-Service -Name "FreightSnapIngestService" `
  -BinaryPathName "C:\path\to\publish\FreightSnapIngestService.exe" `
  -DisplayName "Freight Snap Ingest Service" `
  -StartupType Automatic

Start-Service -Name "FreightSnapIngestService"
```

## Verify

- `Get-Service FreightSnapIngestService` should show `Running`.
- Drop a CSV into `CsvDirectory` and matching images into `ImageDirectory`; within
  two poll intervals the pair should move to `ProcessedDirectory` and a row should
  appear in `dbo.FreightScans`.
- Logs are written under `logs/` next to the service executable.

## Uninstall

```powershell
Stop-Service -Name "FreightSnapIngestService"
sc.exe delete "FreightSnapIngestService"
```
```

- [ ] **Step 6: Commit**

```bash
git add src/FreightSnapIngestService/Program.cs src/FreightSnapIngestService/FreightSnapIngestService.csproj README.md
git commit -m "feat: wire up DI, enable Windows Service hosting, and document deployment"
```

---

### Task 13: Automated end-to-end verification with real-data-derived datasets

**Files:**
- Create: `tests/FreightSnapIngestService.Tests/EndToEnd/EndToEndIngestionTests.cs`

**Interfaces:**
- Consumes every production type from Tasks 1–11 directly, unfaked: `IngestPipeline`, `RetentionCleaner`, `FreightScanRepository`/`IFreightScanRepository`, `SqlServerTestFixture` (Task 7), `CsvScanParser`, `ImageMatcher`, `ScanFileNamer`, `PendingScanTracker`, `FileStabilityTracker`, `FreightIngestOptions`. Only the clock is faked (`FakeTimeProvider`, injected into `PendingScanTracker` and `RetentionCleaner`) for deterministic timing; CSV parsing, image matching, file moves, and SQL Server reads/writes all run for real, against the real local database.
- Produces: no new production types. This is a test-only task, dispatched after Task 12 once the full service exists.

This replaces manual smoke-testing with an automated suite. Its scenarios are
derived directly from real production data inspected in `Demo_Data/SWAD_FILES/*.csv`
and `Demo_Data/Images/*.jpg` — headerless CSV rows, `yyyyMMddHHmmss` timestamps,
`<barcode>_<yyyyMMdd>_<HHmmss>_<n>.<ext>` image names, a noisy barcode field
(`vvhjmfvjhfg724076`), a truncated row shape, and the observed `555`/`5555`
barcode-substring collision — rather than synthetic placeholder values.

- [ ] **Step 1: Write the end-to-end test file**

```csharp
// tests/FreightSnapIngestService.Tests/EndToEnd/EndToEndIngestionTests.cs
using FreightSnapIngestService.Csv;
using FreightSnapIngestService.Db;
using FreightSnapIngestService.Images;
using FreightSnapIngestService.Ingestion;
using FreightSnapIngestService.Naming;
using FreightSnapIngestService.Options;
using FreightSnapIngestService.Pending;
using FreightSnapIngestService.Retention;
using FreightSnapIngestService.Stability;
using FreightSnapIngestService.Tests.Db;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace FreightSnapIngestService.Tests.EndToEnd;

public class EndToEndIngestionTests : IClassFixture<SqlServerTestFixture>, IDisposable
{
    private readonly SqlServerTestFixture _sqlFixture;
    private readonly string _root;
    private readonly FreightIngestOptions _options;
    private readonly FakeTimeProvider _time = new();
    private readonly IFreightScanRepository _repository;
    private readonly IngestPipeline _pipeline;
    private readonly RetentionCleaner _retentionCleaner;

    public EndToEndIngestionTests(SqlServerTestFixture sqlFixture)
    {
        _sqlFixture = sqlFixture;
        _root = Directory.CreateTempSubdirectory("e2e-ingest-tests-").FullName;
        _options = new FreightIngestOptions
        {
            CsvDirectory = Path.Combine(_root, "csv"),
            ImageDirectory = Path.Combine(_root, "images"),
            ProcessedDirectory = Path.Combine(_root, "processed"),
            ErrorDirectory = Path.Combine(_root, "errors"),
            PendingTimeoutMinutes = 30,
            RetentionDays = 30,
        };
        Directory.CreateDirectory(_options.CsvDirectory);
        Directory.CreateDirectory(_options.ImageDirectory);

        _repository = new FreightScanRepository(_sqlFixture.ConnectionString);
        _pipeline = new IngestPipeline(
            _options,
            new CsvScanParser(),
            new ImageMatcher(),
            new ScanFileNamer(),
            new PendingScanTracker(_time),
            new FileStabilityTracker(),
            _repository,
            NullLogger<IngestPipeline>.Instance);
        _retentionCleaner = new RetentionCleaner(_options, _repository, _time);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteCsv(string fileName, string content)
    {
        var path = Path.Combine(_options.CsvDirectory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteImage(string fileName)
    {
        var path = Path.Combine(_options.ImageDirectory, fileName);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    private async Task<(DateTime? ScanDateTime, string? CsvPath, int ImageCount)> ReadScanAsync(string barcode)
    {
        await using var conn = new SqlConnection(_sqlFixture.ConnectionString);
        await conn.OpenAsync();

        await using var scanCmd = conn.CreateCommand();
        scanCmd.CommandText =
            "SELECT TOP 1 Id, ScanDateTime, CsvPath FROM dbo.FreightScans WHERE Barcode = @Barcode ORDER BY Id DESC";
        scanCmd.Parameters.AddWithValue("@Barcode", barcode);
        await using var reader = await scanCmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return (null, null, 0);

        var scanId = reader.GetInt64(0);
        var scanDateTime = reader.GetDateTime(1);
        var csvPath = reader.IsDBNull(2) ? null : reader.GetString(2);
        await reader.CloseAsync();

        await using var imageCmd = conn.CreateCommand();
        imageCmd.CommandText = "SELECT COUNT(*) FROM dbo.FreightScanImages WHERE FreightScanId = @Id";
        imageCmd.Parameters.AddWithValue("@Id", scanId);
        var imageCount = (int)(await imageCmd.ExecuteScalarAsync())!;

        return (scanDateTime, csvPath, imageCount);
    }

    [Fact]
    public async Task Ingests_a_realistic_multi_image_scan_matching_production_CSV_and_image_formats()
    {
        // Real row from Demo_Data/SWAD_FILES/20260804_144435_1.csv: headerless, yyyyMMddHHmmss datetime.
        WriteCsv("scan.csv", "724076,20260804144415,122.0,101.5,128.5,1.591216,216.000\n");
        // Real image naming convention from Demo_Data/Images: <barcode>_<yyyyMMdd>_<HHmmss>_<n>.jpg
        WriteImage("724076_20260804_144420_1.jpg");
        WriteImage("724076_20260804_144420_2.jpg");

        await _pipeline.ProcessOnceAsync(CancellationToken.None); // stability baseline
        await _pipeline.ProcessOnceAsync(CancellationToken.None); // finalizes

        var scan = await ReadScanAsync("724076");
        Assert.Equal(new DateTime(2026, 8, 4, 14, 44, 15), scan.ScanDateTime);
        Assert.Equal(2, scan.ImageCount);
        Assert.NotNull(scan.CsvPath);
        Assert.True(File.Exists(scan.CsvPath));

        var processedFolder = Path.Combine(_options.ProcessedDirectory, "724076_20260804144415");
        Assert.True(File.Exists(Path.Combine(processedFolder, "724076_20260804144415_1.jpg")));
        Assert.True(File.Exists(Path.Combine(processedFolder, "724076_20260804144415_2.jpg")));
    }

    [Fact]
    public async Task Does_not_cross_match_images_between_a_barcode_and_its_digit_substring_collision()
    {
        // Real Demo_Data/Images collision: barcode "555" is a literal substring of barcode "5555".
        WriteCsv("scan.csv", "555,20260807151435,50.0,40.0,30.0,0.060000,5.000\n");
        WriteImage("555_20260807_151435_1.jpg");
        WriteImage("555_20260807_151435_2.jpg");
        WriteImage("5555_20260807_151343_1.jpg");
        WriteImage("5555_20260807_151343_2.jpg");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        var scan = await ReadScanAsync("555");
        Assert.Equal(2, scan.ImageCount); // only its own two images, not the 5555 pair

        // The unrelated barcode's images were never touched by the "555" scan.
        Assert.True(File.Exists(Path.Combine(_options.ImageDirectory, "5555_20260807_151343_1.jpg")));
        Assert.True(File.Exists(Path.Combine(_options.ImageDirectory, "5555_20260807_151343_2.jpg")));
    }

    [Fact]
    public async Task Finalizes_a_noisy_barcode_without_images_after_the_pending_timeout()
    {
        // Real Demo_Data anomaly: a barcode field with extra leading characters
        // (Demo_Data/SWAD_FILES/20260804_144537_1.csv).
        WriteCsv("scan.csv", "vvhjmfvjhfg724076,20260804144524,122.0,101.5,128.5,1.591216,216.000\n");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None); // now pending, no images
        _time.Advance(TimeSpan.FromMinutes(31));
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        var scan = await ReadScanAsync("vvhjmfvjhfg724076");
        Assert.Equal(0, scan.ImageCount);
        Assert.NotNull(scan.CsvPath);
    }

    [Fact]
    public async Task Routes_a_truncated_production_style_row_to_the_error_directory()
    {
        // Real row shape with the trailing Weight field dropped (simulates a truncated write).
        WriteCsv("truncated.csv", "714826,20260812213052,114.0,103.0,167.5,1.966785\n");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_options.ErrorDirectory, "truncated.csv")));
        var scan = await ReadScanAsync("714826");
        Assert.Null(scan.ScanDateTime);
    }

    [Fact]
    public async Task Retention_clears_file_paths_but_keeps_the_scan_row_after_files_expire()
    {
        WriteCsv("scan.csv", "714826,20260812213052,114.0,103.0,167.5,1.966785,215.000\n");
        WriteImage("714826_20260812_213100_1.jpg");

        await _pipeline.ProcessOnceAsync(CancellationToken.None);
        await _pipeline.ProcessOnceAsync(CancellationToken.None);

        var processedFolder = Path.Combine(_options.ProcessedDirectory, "714826_20260812213052");
        Directory.SetLastWriteTimeUtc(processedFolder, DateTime.UtcNow.AddDays(-31));
        _time.SetUtcNow(DateTimeOffset.UtcNow);

        await _retentionCleaner.RunOnceAsync(CancellationToken.None);

        Assert.False(Directory.Exists(processedFolder));
        var scan = await ReadScanAsync("714826");
        Assert.Equal(new DateTime(2026, 8, 12, 21, 30, 52), scan.ScanDateTime); // scan row still present
        Assert.Null(scan.CsvPath); // path cleared
        Assert.Equal(0, scan.ImageCount); // image rows cleared
    }
}
```

- [ ] **Step 2: Run the suite to verify it passes**

Run: `dotnet test tests/FreightSnapIngestService.Tests --filter EndToEndIngestionTests`
Expected: PASS (5 tests) — requires the same reachable SQL Server instance `SqlServerTestFixture` (Task 7) uses

- [ ] **Step 3: Run the full solution test suite once more**

Run: `dotnet test FreightSnapIngestService.sln`
Expected: PASS — every test from Tasks 1–13

- [ ] **Step 4: Commit**

```bash
git add tests/FreightSnapIngestService.Tests/EndToEnd
git commit -m "test: add automated end-to-end verification derived from real production data"
```
