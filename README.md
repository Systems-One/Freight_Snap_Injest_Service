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
