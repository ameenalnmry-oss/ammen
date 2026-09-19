SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.CultureMediaLots', N'U') IS NULL
        THROW 51200, 'Required table dbo.CultureMediaLots does not exist.', 1;
    IF OBJECT_ID(N'dbo.MediaPreparations', N'U') IS NULL
        THROW 51201, 'Required table dbo.MediaPreparations does not exist.', 1;
    IF OBJECT_ID(N'dbo.MediaQualifications', N'U') IS NULL
        THROW 51202, 'Required table dbo.MediaQualifications does not exist.', 1;
    IF OBJECT_ID(N'dbo.EM_Events', N'U') IS NULL
        THROW 51203, 'Required table dbo.EM_Events does not exist.', 1;

    /* Dehydrated-media inventory */
    IF COL_LENGTH(N'dbo.CultureMediaLots', N'InitialStockG') IS NULL
        ALTER TABLE dbo.CultureMediaLots ADD InitialStockG DECIMAL(18,3) NULL;
    IF COL_LENGTH(N'dbo.CultureMediaLots', N'CurrentStockG') IS NULL
        ALTER TABLE dbo.CultureMediaLots ADD CurrentStockG DECIMAL(18,3) NULL;
    IF COL_LENGTH(N'dbo.CultureMediaLots', N'StockStatus') IS NULL
        ALTER TABLE dbo.CultureMediaLots ADD StockStatus NVARCHAR(30) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'PowderQuantityG') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD PowderQuantityG DECIMAL(18,3) NULL;

    IF OBJECT_ID(N'dbo.CultureMediaStockTransactions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaStockTransactions
        (
            StockTransactionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaStockTransactions PRIMARY KEY,
            MediaLotID INT NOT NULL,
            MediaPreparationID INT NULL,
            TransactionType NVARCHAR(40) NOT NULL,
            QuantityChangeG DECIMAL(18,3) NOT NULL,
            BalanceBeforeG DECIMAL(18,3) NULL,
            BalanceAfterG DECIMAL(18,3) NOT NULL,
            ReferenceNo NVARCHAR(80) NULL,
            Reason NVARCHAR(500) NULL,
            PerformedBy NVARCHAR(100) NOT NULL,
            PerformedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaStockTransactions_PerformedAt DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_CultureMediaStockTransactions_Lot FOREIGN KEY(MediaLotID) REFERENCES dbo.CultureMediaLots(MediaLotID),
            CONSTRAINT FK_CultureMediaStockTransactions_Preparation FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID),
            CONSTRAINT CK_CultureMediaStockTransactions_NonZero CHECK(QuantityChangeG <> 0),
            CONSTRAINT CK_CultureMediaStockTransactions_Balances CHECK
            (
                (BalanceBeforeG IS NULL OR BalanceBeforeG >= 0)
                AND BalanceAfterG >= 0
            )
        );
        CREATE INDEX IX_CultureMediaStockTransactions_LotDate
            ON dbo.CultureMediaStockTransactions(MediaLotID, PerformedAt DESC, StockTransactionID DESC);
    END
    ELSE
    BEGIN
        IF COL_LENGTH(N'dbo.CultureMediaStockTransactions', N'BalanceBeforeG') IS NULL
            ALTER TABLE dbo.CultureMediaStockTransactions ADD BalanceBeforeG DECIMAL(18,3) NULL;
        IF COL_LENGTH(N'dbo.CultureMediaStockTransactions', N'Reason') IS NULL
            ALTER TABLE dbo.CultureMediaStockTransactions ADD Reason NVARCHAR(500) NULL;
    END;

    /* Do not invent historical consumption. Preserve current balances and flag unknown balances for reconciliation. */
    EXEC sys.sp_executesql N'
        UPDATE dbo.CultureMediaLots
        SET InitialStockG = COALESCE(InitialStockG, TRY_CONVERT(DECIMAL(18,3), QuantityReceived));

        UPDATE l
        SET CurrentStockG = l.InitialStockG
        FROM dbo.CultureMediaLots l
        WHERE l.CurrentStockG IS NULL
          AND l.InitialStockG IS NOT NULL
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.MediaPreparations p
              WHERE p.MediaLotID = l.MediaLotID
          );

        UPDATE dbo.CultureMediaLots
        SET StockStatus = CASE
            WHEN CurrentStockG IS NULL THEN N''Requires Reconciliation''
            WHEN CurrentStockG <= 0 THEN N''Depleted''
            WHEN UPPER(LTRIM(RTRIM(ISNULL(ReceiptStatus, N'''')))) = N''REJECTED'' THEN N''Rejected''
            WHEN UPPER(LTRIM(RTRIM(ISNULL(ReceiptStatus, N'''')))) IN (N''RELEASED'', N''ACCEPTED'') THEN N''Available''
            ELSE N''Quarantine'' END
        WHERE StockStatus IS NULL OR LTRIM(RTRIM(StockStatus)) = N'''';

        INSERT INTO dbo.CultureMediaStockTransactions
        (MediaLotID, MediaPreparationID, TransactionType, QuantityChangeG, BalanceBeforeG, BalanceAfterG, ReferenceNo, Reason, PerformedBy, PerformedAt)
        SELECT
            l.MediaLotID,
            NULL,
            N''HistoricalOpeningBalance'',
            l.CurrentStockG,
            0,
            l.CurrentStockG,
            l.LotNumber,
            N''Historical balance captured during controlled migration; verify by physical reconciliation.'',
            N''System Migration'',
            SYSUTCDATETIME()
        FROM dbo.CultureMediaLots l
        WHERE l.CurrentStockG IS NOT NULL
          AND l.CurrentStockG > 0
          AND NOT EXISTS
          (
              SELECT 1
              FROM dbo.CultureMediaStockTransactions st
              WHERE st.MediaLotID = l.MediaLotID
          );';

    /* Controlled physical stock reconciliation */
    IF OBJECT_ID(N'dbo.CultureMediaStockReconciliations', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaStockReconciliations
        (
            ReconciliationID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaStockReconciliations PRIMARY KEY,
            MediaLotID INT NOT NULL,
            SystemBalanceG DECIMAL(18,3) NULL,
            PhysicalCountG DECIMAL(18,3) NOT NULL,
            DifferenceG DECIMAL(18,3) NOT NULL,
            Reason NVARCHAR(500) NOT NULL,
            ReconciledBy NVARCHAR(100) NOT NULL,
            ReconciledAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaStockReconciliations_At DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_CultureMediaStockReconciliations_Lot FOREIGN KEY(MediaLotID) REFERENCES dbo.CultureMediaLots(MediaLotID),
            CONSTRAINT CK_CultureMediaStockReconciliations_Physical CHECK(PhysicalCountG >= 0)
        );
        CREATE INDEX IX_CultureMediaStockReconciliations_LotDate
            ON dbo.CultureMediaStockReconciliations(MediaLotID, ReconciledAt DESC, ReconciliationID DESC);
    END;

    /* Independent prepared-media controls and immutable release identities */
    IF COL_LENGTH(N'dbo.MediaPreparations', N'VisualCheckedBy') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD VisualCheckedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'VisualCheckedAt') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD VisualCheckedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'SterilityReviewedBy') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD SterilityReviewedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'SterilityReviewedAt') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD SterilityReviewedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'ReleasedBy') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD ReleasedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'ReleasedAt') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD ReleasedAt DATETIME2(0) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'RejectedBy') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD RejectedBy NVARCHAR(100) NULL;
    IF COL_LENGTH(N'dbo.MediaPreparations', N'RejectedAt') IS NULL
        ALTER TABLE dbo.MediaPreparations ADD RejectedAt DATETIME2(0) NULL;

    /* Direct traceability from every EM event to the exact released prepared-media batch */
    IF COL_LENGTH(N'dbo.EM_Events', N'MediaPreparationID') IS NULL
        ALTER TABLE dbo.EM_Events ADD MediaPreparationID INT NULL;

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.foreign_keys
        WHERE name = N'FK_EM_Events_MediaPreparation'
          AND parent_object_id = OBJECT_ID(N'dbo.EM_Events')
    )
        EXEC sys.sp_executesql N'
        ALTER TABLE dbo.EM_Events WITH CHECK
        ADD CONSTRAINT FK_EM_Events_MediaPreparation
            FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID);';

    IF NOT EXISTS
    (
        SELECT 1 FROM sys.indexes
        WHERE name = N'IX_EM_Events_MediaPreparationID'
          AND object_id = OBJECT_ID(N'dbo.EM_Events')
    )
        EXEC sys.sp_executesql N'
        CREATE INDEX IX_EM_Events_MediaPreparationID
            ON dbo.EM_Events(MediaPreparationID, EventDate DESC)
            WHERE MediaPreparationID IS NOT NULL;';

    /* Safe historical backfill only where an exact prepared-media number or controlled plan link exists. */
    EXEC sys.sp_executesql N'
    UPDATE e
    SET MediaPreparationID = matched.MediaPreparationID
    FROM dbo.EM_Events e
    CROSS APPLY
    (
        SELECT TOP (1) p.MediaPreparationID
        FROM dbo.MediaPreparations p
        WHERE UPPER(LTRIM(RTRIM(p.MediaPreparationNo))) = UPPER(LTRIM(RTRIM(ISNULL(e.MediaLotNo, N''''))))
        ORDER BY p.MediaPreparationID DESC
    ) matched
    WHERE e.MediaPreparationID IS NULL;';

    IF OBJECT_ID(N'dbo.EM_PlanSamples', N'U') IS NOT NULL
       AND COL_LENGTH(N'dbo.EM_PlanSamples', N'MediaPreparationID') IS NOT NULL
    BEGIN
        EXEC sys.sp_executesql N'
        UPDATE e
        SET MediaPreparationID = planMedia.MediaPreparationID
        FROM dbo.EM_Events e
        CROSS APPLY
        (
            SELECT TOP (1) ps.MediaPreparationID
            FROM dbo.EM_PlanSamples ps
            WHERE ps.PlanID = e.PlanID
              AND ps.MediaPreparationID IS NOT NULL
            ORDER BY ps.PlanSampleID
        ) planMedia
        WHERE e.MediaPreparationID IS NULL
          AND e.PlanID IS NOT NULL;';
    END;

    IF OBJECT_ID(N'dbo.CultureMediaPreparationDispositions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaPreparationDispositions
        (
            DispositionID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaPreparationDispositions PRIMARY KEY,
            MediaPreparationID INT NOT NULL,
            DispositionType NVARCHAR(50) NOT NULL,
            PowderQuantityG DECIMAL(18,3) NOT NULL CONSTRAINT DF_CultureMediaPreparationDispositions_Powder DEFAULT (0),
            Reason NVARCHAR(500) NOT NULL,
            DisposedBy NVARCHAR(100) NOT NULL,
            DisposedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaPreparationDispositions_At DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_CultureMediaPreparationDispositions_Preparation
                FOREIGN KEY(MediaPreparationID) REFERENCES dbo.MediaPreparations(MediaPreparationID),
            CONSTRAINT CK_CultureMediaPreparationDispositions_Powder CHECK(PowderQuantityG >= 0)
        );
        CREATE INDEX IX_CultureMediaPreparationDispositions_Preparation
            ON dbo.CultureMediaPreparationDispositions(MediaPreparationID, DisposedAt DESC);
    END;

    /* Configurable qualification requirements. Existing SOP defaults remain active until QA changes them. */
    IF OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaQualificationRequirements
        (
            RequirementID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaQualificationRequirements PRIMARY KEY,
            MediaTypePattern NVARCHAR(100) NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Pattern DEFAULT N'%',
            TestName NVARCHAR(100) NOT NULL,
            IsRequired BIT NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Required DEFAULT (1),
            MinimumRecoveryPercent DECIMAL(9,2) NULL,
            MaximumRecoveryPercent DECIMAL(9,2) NULL,
            EffectiveDate DATE NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Effective DEFAULT CAST(GETDATE() AS date),
            IsActive BIT NOT NULL CONSTRAINT DF_CultureMediaQualificationRequirements_Active DEFAULT (1),
            ApprovedBy NVARCHAR(100) NULL,
            ApprovedAt DATETIME2(0) NULL,
            CONSTRAINT CK_CultureMediaQualificationRequirements_Recovery CHECK
            (
                (MinimumRecoveryPercent IS NULL OR MinimumRecoveryPercent >= 0)
                AND (MaximumRecoveryPercent IS NULL OR MaximumRecoveryPercent >= 0)
                AND (MinimumRecoveryPercent IS NULL OR MaximumRecoveryPercent IS NULL OR MinimumRecoveryPercent <= MaximumRecoveryPercent)
            )
        );
        CREATE UNIQUE INDEX UX_CultureMediaQualificationRequirements_Active
            ON dbo.CultureMediaQualificationRequirements(MediaTypePattern, TestName, EffectiveDate);
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.CultureMediaQualificationRequirements)
    BEGIN
        INSERT INTO dbo.CultureMediaQualificationRequirements
        (MediaTypePattern, TestName, IsRequired, MinimumRecoveryPercent, MaximumRecoveryPercent, EffectiveDate, IsActive, ApprovedBy, ApprovedAt)
        VALUES
        (N'%', N'pH Check', 1, NULL, NULL, CAST(GETDATE() AS date), 1, N'System Baseline - QA confirmation required', SYSUTCDATETIME()),
        (N'%', N'Growth Promotion', 1, 50.00, 200.00, CAST(GETDATE() AS date), 1, N'System Baseline - QA confirmation required', SYSUTCDATETIME()),
        (N'%', N'Indicative Property', 1, NULL, NULL, CAST(GETDATE() AS date), 1, N'System Baseline - QA confirmation required', SYSUTCDATETIME()),
        (N'%', N'Inhibitory Property', 1, NULL, NULL, CAST(GETDATE() AS date), 1, N'System Baseline - QA confirmation required', SYSUTCDATETIME()),
        (N'%', N'Preincubation Check', 1, NULL, NULL, CAST(GETDATE() AS date), 1, N'System Baseline - QA confirmation required', SYSUTCDATETIME());
    END;

    /* Controlled print and reprint history */
    IF OBJECT_ID(N'dbo.CultureMediaPrintHistory', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CultureMediaPrintHistory
        (
            PrintHistoryID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CultureMediaPrintHistory PRIMARY KEY,
            EntityType NVARCHAR(50) NOT NULL,
            EntityID INT NOT NULL,
            RecordNumber NVARCHAR(100) NULL,
            DocumentType NVARCHAR(100) NOT NULL,
            PrintSequence INT NOT NULL,
            PrintedBy NVARCHAR(100) NOT NULL,
            PrintedAt DATETIME2(0) NOT NULL CONSTRAINT DF_CultureMediaPrintHistory_PrintedAt DEFAULT SYSUTCDATETIME(),
            Reason NVARCHAR(500) NULL,
            CONSTRAINT CK_CultureMediaPrintHistory_Sequence CHECK(PrintSequence > 0)
        );
        CREATE UNIQUE INDEX UX_CultureMediaPrintHistory_Sequence
            ON dbo.CultureMediaPrintHistory(EntityType, EntityID, DocumentType, PrintSequence);
        CREATE INDEX IX_CultureMediaPrintHistory_Record
            ON dbo.CultureMediaPrintHistory(RecordNumber, PrintedAt DESC);
    END;

    COMMIT TRANSACTION;

    SELECT
        N'Culture Media global compliance update completed successfully.' AS Result,
        OBJECT_ID(N'dbo.CultureMediaStockTransactions', N'U') AS StockTransactionsObjectID,
        OBJECT_ID(N'dbo.CultureMediaStockReconciliations', N'U') AS StockReconciliationsObjectID,
        OBJECT_ID(N'dbo.CultureMediaPreparationDispositions', N'U') AS DispositionsObjectID,
        OBJECT_ID(N'dbo.CultureMediaQualificationRequirements', N'U') AS QualificationRequirementsObjectID,
        OBJECT_ID(N'dbo.CultureMediaPrintHistory', N'U') AS PrintHistoryObjectID,
        COL_LENGTH(N'dbo.MediaPreparations', N'VisualCheckedBy') AS VisualCheckedByColumn,
        COL_LENGTH(N'dbo.MediaPreparations', N'SterilityReviewedBy') AS SterilityReviewedByColumn,
        COL_LENGTH(N'dbo.MediaPreparations', N'ReleasedBy') AS ReleasedByColumn,
        COL_LENGTH(N'dbo.EM_Events', N'MediaPreparationID') AS EMEventMediaPreparationIDColumn;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
