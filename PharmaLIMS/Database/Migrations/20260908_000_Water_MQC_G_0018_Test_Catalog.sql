SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    /*
       v236 controlled Water test catalog prerequisite.

       Purpose:
       - Ensure that the active Tests master can represent the tests explicitly
         described by the site's MQC-G-0018 v2.0 Water Analysis SOP.
       - Add the CTG-11-02 v02 potable-water contaminant names only as optional
         master-test definitions; they are NOT assigned to a profile by this migration.

       This migration does not create/approve Water profiles, does not set operational
       limits, and does not alter existing Tests rows. Existing synonymous rows are
       preserved and reused by the application authoring helper.
    */

    IF OBJECT_ID(N'dbo.Tests', N'U') IS NULL
        THROW 54800, 'Required table dbo.Tests is missing before 20260908_000.', 1;

    IF COL_LENGTH(N'dbo.Tests', N'TestCode') IS NULL
       OR COL_LENGTH(N'dbo.Tests', N'TestName') IS NULL
       OR COL_LENGTH(N'dbo.Tests', N'TestCategory') IS NULL
       OR COL_LENGTH(N'dbo.Tests', N'Unit') IS NULL
       OR COL_LENGTH(N'dbo.Tests', N'SortOrder') IS NULL
       OR COL_LENGTH(N'dbo.Tests', N'IsActive') IS NULL
        THROW 54801, 'dbo.Tests does not satisfy the controlled Water test-catalog contract.', 1;

    /* MQC-G-0018 v2.0 core/conditional Water tests. */
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'APPEARANCE (COLOR & CLARITY)',N'APPEARANCE',N'COLOR & CLARITY',N'COLOUR & CLARITY'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-APPEAR',N'Appearance (Color & Clarity)',N'Physical',N'Result',1010,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'PH VALUE',N'PH',N'P.H'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-PH',N'pH Value',N'Physical',N'pH',1020,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'CONDUCTIVITY',N'ELECTRICAL CONDUCTIVITY'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-COND',N'Conductivity',N'Physical',N'µS/cm',1030,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'TOTAL DISSOLVED SOLIDS (TDS)',N'TOTAL DISSOLVED SOLIDS',N'TDS'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-TDS',N'Total Dissolved Solids (TDS)',N'Chemical',N'mg/L',1040,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'TOTAL ORGANIC CARBON (TOC)',N'TOTAL ORGANIC CARBON',N'TOC'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-TOC',N'Total Organic Carbon (TOC)',N'Chemical',N'ppb',1050,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'TOTAL AEROBIC MICROBIAL COUNT (TAMC)',N'TOTAL AEROBIC MICROBIAL COUNT',N'TAMC',N'TOTAL VIABLE COUNT'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-TAMC',N'Total Aerobic Microbial Count (TAMC)',N'Microbiological',N'CFU/mL',1060,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'ESCHERICHIA COLI',N'E. COLI',N'E COLI',N'E.COLI'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-ECOLI',N'Escherichia coli',N'Microbiological',N'Absence',1070,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'SALMONELLA')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-SALM',N'Salmonella',N'Microbiological',N'Absence',1080,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'PSEUDOMONAS AERUGINOSA',N'P. AERUGINOSA'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-PAER',N'Pseudomonas aeruginosa',N'Microbiological',N'Absence',1090,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'STAPHYLOCOCCUS AUREUS',N'S. AUREUS'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-SAUR',N'Staphylococcus aureus',N'Microbiological',N'Absence',1100,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'BURKHOLDERIA CEPACIA COMPLEX',N'BURKHOLDERIA CEPACIA',N'BCC'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-BCC',N'Burkholderia cepacia complex',N'Microbiological',N'Absence',1110,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'BACTERIAL ENDOTOXIN TEST (BET)',N'BACTERIAL ENDOTOXIN TEST',N'BET'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-BET',N'Bacterial Endotoxin Test (BET)',N'Microbiological',N'EU/mL',1120,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'HARDNESS',N'TOTAL HARDNESS',N'CALCIUM & MAGNESIUM',N'CALCIUM AND MAGNESIUM'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-HARD',N'Hardness',N'Chemical',N'mg/L as CaCO3',1130,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'TOTAL SUSPENDED SOLIDS (TSS)',N'TOTAL SUSPENDED SOLIDS',N'TSS'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-TSS',N'Total Suspended Solids (TSS)',N'Chemical',N'mg/L',1140,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'ACIDITY')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-ACID',N'Acidity',N'Chemical',N'Result',1150,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'ALKALINITY')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-ALK',N'Alkalinity',N'Chemical',N'Result',1160,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'AMMONIUM',N'AMMONIA'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-AMMON',N'Ammonium',N'Chemical',N'Result',1170,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'HEAVY METALS',N'HEAVY METALS (AS PB)',N'HEAVY METALS AS PB'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-HM',N'Heavy Metals (as Pb)',N'Chemical',N'Result',1180,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'CHLORIDE',N'CHLORIDES'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-CL',N'Chlorides',N'Chemical',N'Result',1190,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'NITRATE',N'NITRATES'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-NO3',N'Nitrates',N'Chemical',N'Result',1200,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'SULPHATE',N'SULPHATES',N'SULFATE',N'SULFATES'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-SO4',N'Sulphates',N'Chemical',N'Result',1210,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'OXIDISABLE SUBSTANCES',N'OXIDIZABLE SUBSTANCES'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-OXID',N'Oxidisable Substances',N'Chemical',N'Result',1220,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'RESIDUE ON EVAPORATION',N'RESIDUE ON EVAPORATION TEST'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-RES-EVAP',N'Residue on Evaporation',N'Chemical',N'mg/100mL',1230,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'RESIDUAL CHLORINE',N'FREE CHLORINE',N'FREE RESIDUAL CHLORINE',N'CHLORINE'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-FCL',N'Residual Chlorine',N'Chemical',N'mg/L',1240,1);

    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'ODOR',N'ODOUR'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive)
        VALUES(N'WTR-ODOR',N'Odor',N'Physical',N'Result',1250,1);

    /*
       Optional PTW contaminant definitions from CTG-11-02 v02 Annexure CTG 11/A3.
       These rows remain ordinary inactive-by-profile master tests until a controlled
       profile author explicitly loads/adopts that source.
    */
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'ARSENIC')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-AS',N'Arsenic',N'Chemical',N'ppm',1300,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'BARIUM')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-BA',N'Barium',N'Chemical',N'ppm',1310,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'CADMIUM')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-CD',N'Cadmium',N'Chemical',N'ppm',1320,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'CHROMIUM')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-CR',N'Chromium',N'Chemical',N'ppm',1330,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'LEAD')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-PB',N'Lead',N'Chemical',N'ppm',1340,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'SELENIUM')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-SE',N'Selenium',N'Chemical',N'ppm',1350,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'MERCURY')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-HG',N'Mercury',N'Chemical',N'ppm',1360,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'FLUORIDE')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-F',N'Fluoride',N'Chemical',N'ppm',1370,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'NITRATES (AS N)',N'NITRATE (AS N)'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-NO3N',N'Nitrates (as N)',N'Chemical',N'ppm',1375,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'NITRITES (AS N)',N'NITRITE (AS N)'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-NO2',N'Nitrites (as N)',N'Chemical',N'ppm',1380,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'TURBIDITY')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-TURB',N'Turbidity',N'Physical',N'NTU',1390,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName)))=N'ENDRIN')
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-ENDRIN',N'Endrin',N'Chemical',N'ppm',1410,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'2,4 DDT',N'2,4-DDT'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-24DDT',N'2,4 DDT',N'Chemical',N'ppm',1420,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'4,4 DDT',N'4,4-DDT'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-44DDT',N'4,4 DDT',N'Chemical',N'ppm',1430,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'GROSS ALPHA AND BETA ACTIVITY',N'GROSS Α AND GROSS Β'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-RAD-AB',N'Gross Alpha and Beta Activity',N'Chemical',N'pCi/L',1440,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'RADIUM-226 AND RADIUM-228',N'RA-226 AND RA-228'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-RA',N'Radium-226 and Radium-228',N'Chemical',N'pCi/L',1450,1);
    IF NOT EXISTS (SELECT 1 FROM dbo.Tests WHERE UPPER(LTRIM(RTRIM(TestName))) IN (N'TOTAL COLIFORMS',N'TOTAL COLIFORM'))
        INSERT dbo.Tests(TestCode,TestName,TestCategory,Unit,SortOrder,IsActive) VALUES(N'PTW-COLI',N'Total Coliforms',N'Microbiological',N'Count',1400,1);

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
