SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
    Controlled clarification of the oral-tablet microbiology reference text.

    This migration does NOT change any microbiological result, historical
    PRM_SampleTests snapshot, acceptance limit, result type, unit, or test panel.

    Rationale:
      - USP <1111> provides the acceptance criteria for non-aqueous oral
        preparations: TAMC NMT 10^3 CFU/g, TYMC NMT 10^2 CFU/g, and
        Escherichia coli absent in 1 g.
      - The additional Salmonella spp., Staphylococcus aureus,
        Pseudomonas aeruginosa, and Candida albicans tests in the Medica
        oral-tablet profile are retained as site-defined objectionable-organism
        controls. USP <62> is referenced as the compendial test method, without
        implying that USP <1111> directly requires those additional organisms
        for non-aqueous oral preparations.

    Migration 20260830_001 remains unchanged to preserve the checksum of the
    previously released controlled migration. This corrective migration updates
    only the current version-2 master-data reference text.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.PRM_SpecificationTests',N'U') IS NULL
        THROW 53510, 'Required table dbo.PRM_SpecificationTests is missing.', 1;

    DECLARE @Profiles TABLE
    (
        SpecificationNo NVARCHAR(120) NOT NULL PRIMARY KEY,
        SampleCategory NVARCHAR(40) NOT NULL
    );

    INSERT @Profiles(SpecificationNo,SampleCategory)
    VALUES
      (N'MIC-IP-ORAL-TABLET-STD-001',N'Production / In-Process'),
      (N'MIC-FP-ORAL-TABLET-STD-001',N'Finished Product'),
      (N'MIC-ST-ORAL-TABLET-STD-001',N'Stability');

    IF EXISTS
    (
        SELECT 1
        FROM @Profiles p
        WHERE (SELECT COUNT(1)
               FROM dbo.PRM_SpecificationTests s
               WHERE s.SpecificationNo=p.SpecificationNo
                 AND s.SampleCategory=p.SampleCategory
                 AND s.VersionNo=2) <> 7
    )
        THROW 53511, 'The controlled oral-tablet standard profile version 2 is missing or incomplete.', 1;

    UPDATE s
    SET CompendialReference =
        CASE UPPER(LTRIM(RTRIM(s.TestCode)))
            WHEN N'TAMC' THEN
                N'USP <61>/<1111>; non-aqueous oral preparation acceptance criterion'
            WHEN N'TYMC' THEN
                N'USP <61>/<1111>; non-aqueous oral preparation acceptance criterion'
            WHEN N'ECOLI' THEN
                N'USP <62>/<1111>; E. coli absent in 1 g for non-aqueous oral preparations'
            WHEN N'SALMONELLA' THEN
                N'USP <62> method; site-defined objectionable-organism control'
            WHEN N'SAUREUS' THEN
                N'USP <62> method; site-defined objectionable-organism control'
            WHEN N'PAERUGINOSA' THEN
                N'USP <62> method; site-defined objectionable-organism control'
            WHEN N'CALBICANS' THEN
                N'USP <62> method; site-defined objectionable-organism control'
            ELSE s.CompendialReference
        END
    FROM dbo.PRM_SpecificationTests s
    INNER JOIN @Profiles p
        ON p.SpecificationNo=s.SpecificationNo
       AND p.SampleCategory=s.SampleCategory
    WHERE s.VersionNo=2;

    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests s
        INNER JOIN @Profiles p
            ON p.SpecificationNo=s.SpecificationNo
           AND p.SampleCategory=s.SampleCategory
        WHERE s.VersionNo=2
          AND
          (
              (UPPER(LTRIM(RTRIM(s.TestCode))) IN (N'TAMC',N'TYMC')
               AND s.CompendialReference<>N'USP <61>/<1111>; non-aqueous oral preparation acceptance criterion')
              OR
              (UPPER(LTRIM(RTRIM(s.TestCode)))=N'ECOLI'
               AND s.CompendialReference<>N'USP <62>/<1111>; E. coli absent in 1 g for non-aqueous oral preparations')
              OR
              (UPPER(LTRIM(RTRIM(s.TestCode))) IN (N'SALMONELLA',N'SAUREUS',N'PAERUGINOSA',N'CALBICANS')
               AND s.CompendialReference<>N'USP <62> method; site-defined objectionable-organism control')
          )
    )
        THROW 53512, 'The oral-tablet compendial-reference clarification could not be applied correctly.', 1;

    /* Acceptance limits remain unchanged by this reference-only correction. */
    IF EXISTS
    (
        SELECT 1
        FROM dbo.PRM_SpecificationTests s
        INNER JOIN @Profiles p
            ON p.SpecificationNo=s.SpecificationNo
           AND p.SampleCategory=s.SampleCategory
        WHERE s.VersionNo=2
          AND
          (
              (UPPER(LTRIM(RTRIM(s.TestCode)))=N'TAMC'
               AND (ISNULL(s.SpecificationLimit,-1)<>CONVERT(DECIMAL(18,3),1000)
                    OR s.SpecificationText<>N'NMT 1000 CFU/g'))
              OR
              (UPPER(LTRIM(RTRIM(s.TestCode)))=N'TYMC'
               AND (ISNULL(s.SpecificationLimit,-1)<>CONVERT(DECIMAL(18,3),100)
                    OR s.SpecificationText<>N'NMT 100 CFU/g'))
          )
    )
        THROW 53513, 'The oral-tablet acceptance limits changed unexpectedly during reference clarification.', 1;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
