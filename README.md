# Freight Snap Ingest Service

A Windows Service that ingests CSV scan records and matched barcode images from a freight dimensioning/scale system, writes them to Microsoft SQL Server, organizes the source files into a processed archive, and cleans up expired output on a configurable retention schedule — while keeping every scan's measurement data permanently, even after its files are gone.

[![Build, Test and Package Installer](https://github.com/Jwagener1/Freight_Snap_Injest_Service/actions/workflows/build-and-release.yml/badge.svg)](https://github.com/Jwagener1/Freight_Snap_Injest_Service/actions/workflows/build-and-release.yml) [![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/) [![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## Overview

Upstream freight-scanning hardware drops two kinds of files into watch folders: a small CSV per scan (barcode, timestamp, dimensions, weight) and one or more barcode photos. This service:

1. **Polls and parses** incoming CSVs once their size is stable across two consecutive polls (guarding against reading a file mid-write).
2. **Matches** each scan to its barcode photos, waiting up to a configurable timeout for images that arrive slightly after their CSV.
3. **Persists** the scan and its image references to SQL Server in a single transaction.
4. **Archives** the CSV and images into a per-scan processed folder, renamed to a consistent `<Barcode>_<Timestamp>` convention.
5. **Retires** processed and error output after a configurable retention window — deleting files and clearing their database path references, but never the scan's measurement data itself.

Downstream systems (e.g. a WMS) never need FTP/API integration with this service — they simply query the database for scans with live file paths.

## Architecture

Two independent `BackgroundService` loops, wired together with dependency injection:

| Component | Responsibility |
|---|---|
| `IngestWorker` | Polls on a configurable interval, driving `IngestPipeline` |
| `IngestPipeline` | Orchestrates: stability check → parse → image match → finalize/rollback |
| `CsvScanParser` | Parses the headerless, positional scan CSV format |
| `ImageMatcher` | Matches barcode photos to a scan via digit-boundary filename matching |
| `ScanFileNamer` | Derives the archived folder/file naming convention |
| `PendingScanTracker` / `FileStabilityTracker` | Time-based state for the poll loop |
| `FreightScanRepository` | Parameterized ADO.NET access to SQL Server |
| `RetentionWorker` | Runs hourly, driving `RetentionCleaner` |
| `RetentionCleaner` | Deletes expired output; clears DB paths first so a transient failure is self-healing |
| `StartupValidation` | Logs resolved configuration and warns on missing directories at boot |

Each collaborator is a small, single-responsibility class with a narrow interface, independently unit-tested. An [automated end-to-end suite](tests/FreightSnapIngestService.Tests/EndToEnd) exercises the real, unfaked pipeline against a real SQL Server instance, using scenarios derived from actual production data samples — including a barcode-substring collision (`555` vs `5555`) that a naive matcher would get wrong.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- A reachable SQL Server instance (LocalDB, Express, or full) for running the database-backed tests and for deployment

## Project Structure

```
src/FreightSnapIngestService/
  Csv/          CSV parsing
  Images/       Barcode-to-image matching
  Naming/       Archived file/folder naming
  Pending/      Pending-scan timeout tracking
  Stability/    File-write-stability detection
  Db/           SQL Server schema + repository
  Ingestion/    Pipeline orchestration + polling worker
  Retention/    Cleanup logic + hourly worker
  Options/      Strongly-typed configuration
  Program.cs    DI wiring, Windows Service hosting
tests/FreightSnapIngestService.Tests/
  EndToEnd/     Real-stack tests against a real database
  ...           Unit tests mirroring the src/ layout
db/schema.sql   Deployable SQL Server schema
docs/           Design spec and implementation plan
```

## Building and Testing

```bash
dotnet build FreightSnapIngestService.sln
dotnet test FreightSnapIngestService.sln
```

The database-backed tests (repository and end-to-end suites) connect to `(localdb)\MSSQLLocalDB` by default and create/drop their own disposable databases per test run — this matches what's pre-installed on GitHub-hosted Windows runners, so CI works out of the box. To point at a different reachable instance locally (e.g. a full local SQL Server install), set the `FREIGHT_TEST_SQL_SERVER` environment variable to its server name (e.g. `.` for the local default instance) before running `dotnet test`.

## Continuous Integration

```
Push to main → [Run Tests] → Pass? → [Build + Package Installer] → GitHub Release
                                ↓ Fail
                            Build blocked
```

Pull requests targeting `main` run the same test job. `main` is protected: merging requires
an open pull request with the test job green — direct pushes and merges with failing or pending
tests are rejected.

## Configure

Edit `src/FreightSnapIngestService/appsettings.json` (or an environment-specific override) with real directory paths and connection string before publishing.

The `FreightIngest` section supports the following keys:

| Key                   | Meaning                                                                 | Default |
|------------------------|--------------------------------------------------------------------------|---------|
| `CsvDirectory`         | Folder polled for incoming scan CSV files.                              | —       |
| `ImageDirectory`       | Folder polled for incoming barcode images to match against pending CSVs.| —       |
| `ProcessedDirectory`   | Folder where successfully finalized CSV+image sets are moved.           | —       |
| `ErrorDirectory`       | Folder where malformed or duplicate/re-delivered CSVs are moved.        | —       |
| `PollIntervalSeconds`  | How often the ingest pipeline polls `CsvDirectory` for new files.       | `10`    |
| `PendingTimeoutMinutes`| How long a CSV waits for matching images before finalizing without them.| `30`    |
| `RetentionDays`        | How long processed folders and error files are kept before cleanup.     | `30`    |
| `ConnectionString`     | SQL Server connection string used for `dbo.FreightScans` inserts.       | —       |

`CsvDirectory`, `ImageDirectory`, `ProcessedDirectory`, and `ErrorDirectory` should be
absolute paths. At startup the service logs the resolved configuration once
(with the connection string redacted) and logs a warning for any of these four
directories that doesn't currently exist on disk.

## Deploy the Database Schema

Run `db/schema.sql` against the target SQL Server database once, before starting
the service for the first time.

## Publish and Install as a Windows Service

### Installer (recommended)

Every push to `main` that passes CI builds `Freight_Snap_Ingest_Service_Setup.exe` and attaches
it to a [GitHub Release](https://github.com/Jwagener1/Freight_Snap_Injest_Service/releases). Run
it as Administrator — it publishes the app, installs it as a Windows Service
(`FreightSnapIngestService`, auto-start with restart-on-failure), and preserves an existing
`appsettings.json` across upgrades. Then apply `db/schema.sql` (included alongside the install)
and edit `appsettings.json` in the install directory as described above.

### Manual

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

## Design Documentation

- [Design spec](docs/superpowers/specs/2026-08-13-freight-snap-ingest-service-design.md) — architecture and requirements
- [Implementation plan](docs/superpowers/plans/2026-08-13-freight-snap-ingest-service.md) — task-by-task build record

## License

MIT — see [LICENSE](LICENSE).
