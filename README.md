# Freight Snap Ingest Service

Windows Service that ingests CSV scan records and matched barcode images
into SQL Server. See `docs/superpowers/specs/2026-08-13-freight-snap-ingest-service-design.md`
for the full design.

## Configure

Edit `src/FreightSnapIngestService/appsettings.json` (or an environment-specific
override) with real directory paths and connection string before publishing.

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
