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
