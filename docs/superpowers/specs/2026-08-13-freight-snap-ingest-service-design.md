# Freight Snap Ingest Service — Design

## Overview

A Windows Service that watches two directories — one for CSV scan records
and one for barcode images from a freight dimensioning/scale system — and:

1. Parses each CSV and matches it to its images by barcode.
2. Writes the scan data and file locations to a Microsoft SQL Server
   database.
3. Renames and moves the CSV and its matched images into a processed
   folder.
4. Deletes processed (and error) output after a configurable retention
   period, clearing only the stored file-path columns from the database
   while keeping the scan measurement data permanently.

The database is the integration point for downstream consumers (e.g. a
WMS upload process): they query `FreightScans` / `FreightScanImages` for
rows with live paths rather than scanning folders directly. No FTP/API
client code lives in this service.

## Goals

- Reliably ingest CSV + image pairs dropped by an upstream scanning
  system into SQL Server.
- Tolerate images arriving slightly after their CSV (common with
  camera/PLC timing skew) without losing or duplicating data.
- Keep a permanent record of every scan even after the underlying files
  are deleted by retention.
- Run unattended as a standard Windows Service, restartable and
  observable via logs.

## Non-goals

- Uploading images to a WMS via API or FTP — that is a separate,
  already-existing/future process that reads from the database this
  service populates.
- Handling CSV files containing multiple scan rows — each CSV is
  produced as a single scan/event.
- Deduplicating repeat scans of the same barcode — every scan is
  recorded as a new row.

## Architecture

A .NET 8 Worker Service (`Microsoft.Extensions.Hosting`,
`BackgroundService`) running two independent timer-driven loops:

- **Ingest loop** — polls `CsvDirectory` on `PollIntervalSeconds` (default
  10s). Polling, not `FileSystemWatcher`, was chosen for robustness
  against partial writes and network-share quirks common with
  scanner/label PC drop folders.
- **Retention loop** — runs hourly, deleting expired processed/error
  output and clearing the corresponding database path columns.

Both loops share a SQL Server data-access component
(`Microsoft.Data.SqlClient`, plain parameterized ADO.NET — no ORM,
since the write surface is a single-row insert plus a bounded cleanup
query).

## Configuration

`appsettings.json`:

| Key | Purpose |
|---|---|
| `CsvDirectory` | Inbound CSV drop folder |
| `ImageDirectory` | Inbound image drop folder |
| `ProcessedDirectory` | Destination for successfully processed CSV+image sets |
| `ErrorDirectory` | Destination for CSVs that fail to parse |
| `PollIntervalSeconds` | Ingest loop interval (default 10) |
| `PendingTimeoutMinutes` | How long to wait for images before processing a CSV without them (default 30) |
| `RetentionDays` | Age at which processed/error output is deleted (default 30) |
| `ConnectionStrings:FreightDb` | SQL Server connection string |

## Ingest Flow

For each poll cycle:

1. **Discover** CSV files in `CsvDirectory`. A file is only considered
   ready when its size is unchanged between two consecutive polls
   (guards against reading a file mid-copy).
2. **Parse** the CSV: `Barcode, DateTime, Length, Width, Height, Volume,
   Weight`.
   - On parse failure (missing/invalid columns) → move the file
     unchanged to `ErrorDirectory`, log the reason, and stop processing
     it. It is not retried.
3. **Match images**: scan `ImageDirectory` for files whose name contains
   the parsed barcode. Zero, one, or many matches are all valid.
4. **Decide whether to finalize**:
   - If at least one image matched → finalize now.
   - If zero images matched and the CSV has been pending less than
     `PendingTimeoutMinutes` → leave it in place, record/refresh a
     `(barcode, firstSeenUtc)` entry in an in-memory pending list, and
     re-check on the next poll.
   - If zero images matched and `PendingTimeoutMinutes` has elapsed →
     finalize anyway with no images, logging that none were found.
5. **Finalize**:
   - Rename the CSV to `<Barcode>_<DateTime>.csv` (DateTime formatted
     `yyyyMMddHHmmss`).
   - Rename each matched image to
     `<Barcode>_<DateTime>_<n>.<original-extension>`, `n` starting at 1
     in the order the images were found.
   - Move the renamed CSV and images into
     `ProcessedDirectory/<Barcode>_<DateTime>/`.
   - Insert one row into `FreightScans` (including the final `CsvPath`)
     and one row per image into `FreightScanImages` (each with its final
     `ImagePath`), in a single transaction.
   - Remove the entry from the in-memory pending list, if present.
6. **On database failure** during finalize: the rename/move and the DB
   insert are treated as a single logical step. If the insert fails
   after the files were already renamed and moved, undo the move —
   rename the files back and move them back to `CsvDirectory` /
   `ImageDirectory` — so the CSV is picked up and retried from step 1 on
   the next poll cycle. Log the error. There is no separate quarantine
   path for DB failures — persistent DB outages must be visible via
   logs/service health rather than silently diverted.

## Database Schema

```sql
CREATE TABLE dbo.FreightScans (
    Id           BIGINT IDENTITY PRIMARY KEY,
    Barcode      VARCHAR(64)    NOT NULL,
    ScanDateTime DATETIME2      NOT NULL,
    Length       DECIMAL(10,3)  NULL,
    Width        DECIMAL(10,3)  NULL,
    Height       DECIMAL(10,3)  NULL,
    Volume       DECIMAL(10,3)  NULL,
    Weight       DECIMAL(10,3)  NULL,
    CsvPath      NVARCHAR(400)  NULL,   -- cleared to NULL by retention once the file is deleted
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
```

`FreightScans` rows are never updated except by retention clearing
`CsvPath`. `FreightScanImages` rows are deleted entirely by retention
(there is nothing meaningful left once the image file and its path are
gone).

## Retention / Cleanup

Once per hour:

1. For each subfolder of `ProcessedDirectory` (named
   `<Barcode>_<DateTime>`) older than `RetentionDays`:
   - Delete the folder and its contents from disk.
   - `DELETE` all `FreightScanImages` rows whose `ImagePath` was under
     that folder.
   - `UPDATE FreightScans SET CsvPath = NULL` for the scan whose
     `CsvPath` was under that folder.
   - The `FreightScans` row itself (barcode, dimensions, weight,
     timestamps) is never deleted.
2. For each file directly under `ErrorDirectory` older than
   `RetentionDays`: delete it. Error-folder files have no DB rows to
   clean up, since parse failures never reach the insert step.

## Logging

Serilog, writing to:
- A rolling file under a `logs/` folder next to the service binary.
- The console/stdout, so output is visible via `sc.exe query` tooling
  and can be redirected to the Windows Event Log if needed.

Every finalize, skip, pending-timeout, parse error, and DB error is
logged with the barcode and file name(s) involved.

## Testing Strategy

- **Unit tests**: CSV parsing (valid rows, missing columns, malformed
  numbers), barcode-to-image filename matching, rename/path generation,
  pending-timeout decision logic.
- **Integration tests**: DB insert path (scan + image rows created
  correctly in a single transaction) and retention cleanup (files
  deleted, `FreightScanImages` rows removed, `FreightScans.CsvPath`
  nulled, `FreightScans` row otherwise intact) against a LocalDB or test
  SQL Server instance.
