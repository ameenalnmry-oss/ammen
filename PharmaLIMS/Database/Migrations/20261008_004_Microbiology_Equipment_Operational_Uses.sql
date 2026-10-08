SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.LabEquipmentOperationalUses',N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabEquipmentOperationalUses
    (
        OperationalUseID INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_LabEquipmentOperationalUses PRIMARY KEY,
        EquipmentID INT NOT NULL,
        UseCategory NVARCHAR(80) NOT NULL,
        UseCode NVARCHAR(100) NOT NULL,
        UseDescription NVARCHAR(500) NOT NULL,
        EvidenceSource NVARCHAR(250) NOT NULL,
        SortOrder INT NOT NULL CONSTRAINT DF_LabEquipmentOperationalUses_SortOrder DEFAULT(100),
        IsActive BIT NOT NULL CONSTRAINT DF_LabEquipmentOperationalUses_IsActive DEFAULT(1),
        CreatedBy NVARCHAR(100) NOT NULL,
        CreatedAt DATETIME2(0) NOT NULL CONSTRAINT DF_LabEquipmentOperationalUses_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_LabEquipmentOperationalUses_Equipment
            FOREIGN KEY(EquipmentID) REFERENCES dbo.LabEquipment(EquipmentID),
        CONSTRAINT UQ_LabEquipmentOperationalUses UNIQUE(EquipmentID,UseCode)
    );

    CREATE INDEX IX_LabEquipmentOperationalUses_Equipment
        ON dbo.LabEquipmentOperationalUses(EquipmentID,IsActive,SortOrder,OperationalUseID);
END;

-- Normalize the four incubators so later LES/device selection can distinguish their controlled temperature roles.
UPDATE dbo.LabEquipment SET EquipmentType=N'Incubator 55C',UpdatedBy=N'CONTROLLED-MIGRATION',UpdatedAt=SYSUTCDATETIME()
WHERE EquipmentCode=N'MIC-EQ-015' AND EquipmentType=N'Incubator';
UPDATE dbo.LabEquipment SET EquipmentType=N'Incubator 40-45C',UpdatedBy=N'CONTROLLED-MIGRATION',UpdatedAt=SYSUTCDATETIME()
WHERE EquipmentCode=N'MIC-EQ-016' AND EquipmentType=N'Incubator';
UPDATE dbo.LabEquipment SET EquipmentType=N'Incubator 20-25C',UpdatedBy=N'CONTROLLED-MIGRATION',UpdatedAt=SYSUTCDATETIME()
WHERE EquipmentCode=N'MIC-EQ-017' AND EquipmentType=N'Incubator';
UPDATE dbo.LabEquipment SET EquipmentType=N'Incubator 30-35C',UpdatedBy=N'CONTROLLED-MIGRATION',UpdatedAt=SYSUTCDATETIME()
WHERE EquipmentCode=N'MIC-EQ-018' AND EquipmentType=N'Incubator';

DECLARE @Uses TABLE
(
    EquipmentCode NVARCHAR(50) NOT NULL,
    UseCategory NVARCHAR(80) NOT NULL,
    UseCode NVARCHAR(100) NOT NULL,
    UseDescription NVARCHAR(500) NOT NULL,
    EvidenceSource NVARCHAR(250) NOT NULL,
    SortOrder INT NOT NULL
);

INSERT @Uses VALUES
(N'MIC-EQ-001',N'Testing',N'ANTIBIOTIC_ZONE_READING',N'Antibiotic inhibition-zone measurement / reading.',N'Instrument List + equipment-specific verification profile',10),
(N'MIC-EQ-002',N'Media Preparation',N'MEDIA_TEMPERATURE_HOLDING',N'Temperature holding or melting of microbiological media when applicable.',N'MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-003',N'Sample / Media Preparation',N'WEIGHING',N'Weighing of sample, diluent, media components, or supplements.',N'MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-004',N'Testing',N'PH_MEASUREMENT',N'pH measurement for controlled laboratory testing.',N'Instrument List + MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-004',N'Media Preparation',N'MEDIA_PH_CHECK',N'pH check of prepared media when the approved method requires it.',N'MEDICA EQDOC controlled-use profile',20),
(N'MIC-EQ-005',N'Environmental Monitoring',N'ACTIVE_AIR_SAMPLING',N'Active viable air sampling at monitored locations.',N'Instrument List + MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-006',N'Sample Preparation',N'LIQUID_DISPENSING_20_200UL',N'Controlled liquid dispensing in the 20-200 uL range.',N'Instrument List',10),
(N'MIC-EQ-007',N'Sample Preparation',N'LIQUID_DISPENSING_100_1000UL',N'Controlled liquid dispensing in the 100-1000 uL range.',N'Instrument List',10),
(N'MIC-EQ-008',N'Sample Preparation',N'LIQUID_DISPENSING_5_50UL',N'Controlled liquid dispensing in the 5-50 uL range.',N'Instrument List',10),
(N'MIC-EQ-009',N'Observation / Identification',N'MICROSCOPIC_EXAMINATION',N'Microscopic examination with live viewing and image documentation where applicable.',N'Instrument List',10),
(N'MIC-EQ-010',N'Result Reading',N'COLONY_COUNTING',N'Colony counting and microbiological result reading.',N'MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-011',N'Qualification / Calibration',N'TEMPERATURE_MAPPING',N'Temperature calibration, verification, or mapping support within the logger range.',N'Instrument List',10),
(N'MIC-EQ-012',N'Environmental / Qualification',N'NONVIABLE_PARTICLE_COUNTING',N'Non-viable particle counting across the instrument size channels.',N'Instrument List',10),
(N'MIC-EQ-013',N'Testing',N'CONDUCTIVITY_MEASUREMENT',N'Conductivity measurement for controlled laboratory testing.',N'Instrument List + MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-014',N'Glassware Preparation',N'GLASSWARE_DRYING',N'Drying / thermal treatment of laboratory glassware within the approved procedure.',N'Instrument List',10),
(N'MIC-EQ-015',N'Incubation',N'INCUBATION_55C',N'Controlled incubation at the validated 55 C set point when required by an approved method.',N'Instrument List',10),
(N'MIC-EQ-016',N'Incubation',N'INCUBATION_40_45C',N'Controlled incubation in the validated 40-45 C range when required by an approved method.',N'Instrument List + MEDICA EQDOC subculture profile',10),
(N'MIC-EQ-017',N'Incubation',N'FUNGAL_INCUBATION_20_25C',N'Fungal / yeast-mold incubation in the controlled 20-25 C range.',N'MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-017',N'Environmental Monitoring',N'EM_FUNGAL_INCUBATION',N'Environmental monitoring fungal incubation in the controlled 20-25 C range.',N'MEDICA EQDOC controlled-use profile',20),
(N'MIC-EQ-018',N'Incubation',N'BACTERIAL_INCUBATION_30_35C',N'Bacterial incubation in the controlled 30-35 C range.',N'MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-018',N'Environmental Monitoring',N'EM_BACTERIAL_INCUBATION',N'Environmental monitoring bacterial incubation in the controlled 30-35 C range.',N'MEDICA EQDOC controlled-use profile',20),
(N'MIC-EQ-019',N'Sterilization',N'MEDIA_MATERIAL_STERILIZATION',N'Sterilization of prepared media and applicable laboratory materials using controlled cycles.',N'Instrument List + MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-020',N'Deactivation',N'BIOLOGICAL_WASTE_DEACTIVATION',N'Deactivation / destruction of biological waste by validated autoclave cycle.',N'Instrument List',10),
(N'MIC-EQ-021',N'Material Transfer',N'PASSBOX_INCUBATION_OBSERVATION',N'Controlled material transfer from Incubation to Observation room.',N'Instrument List',10),
(N'MIC-EQ-022',N'Material Transfer',N'PASSBOX_CULTURE_PASSAGE',N'Controlled material transfer from Culture Handling to Passage.',N'Instrument List',10),
(N'MIC-EQ-023',N'Material Transfer',N'PASSBOX_OBSERVATION_DEACTIVATION',N'Controlled material transfer from Observation to Media Deactivation room.',N'Instrument List',10),
(N'MIC-EQ-024',N'Material Transfer',N'PASSBOX_COOLING_MLT',N'Controlled material transfer from Cooling Zone to MLT room.',N'Instrument List',10),
(N'MIC-EQ-025',N'Material Transfer',N'PASSBOX_MLT_INCUBATION',N'Controlled material transfer from MLT area to Incubation area.',N'Instrument List',10),
(N'MIC-EQ-026',N'Material Transfer',N'PASSBOX_COOLING_CULTURE',N'Controlled material transfer from Cooling Zone to Culture Handling.',N'Instrument List',10),
(N'MIC-EQ-027',N'Storage',N'MEDIA_CONTROLLED_STORAGE',N'Controlled storage of microbiology media with temperature monitoring.',N'Instrument List',10),
(N'MIC-EQ-028',N'Aseptic Handling',N'MLT_UNIDIRECTIONAL_AIRFLOW',N'Unidirectional airflow support for controlled microbiological handling in the MLT room.',N'Instrument List + MEDICA EQDOC aseptic-handling profile',10),
(N'MIC-EQ-029',N'Aseptic Handling',N'CULTURE_UNIDIRECTIONAL_AIRFLOW',N'Unidirectional airflow support for controlled culture handling.',N'Instrument List + MEDICA EQDOC aseptic-handling profile',10),
(N'MIC-EQ-030',N'Water Microbiology',N'MEMBRANE_FILTRATION_VACUUM',N'Vacuum support for membrane filtration of water samples.',N'MEDICA EQDOC controlled-use profile',10),
(N'MIC-EQ-031',N'Sanitization',N'DISINFECTANT_FOGGING',N'Disinfectant fogging for microbiology area surface and air coverage under the approved sanitation procedure.',N'Instrument List',10),
(N'MIC-EQ-032',N'Sample Preparation',N'SAMPLE_MIXING',N'High-speed mixing / homogenization of small microbiology samples.',N'Instrument List',10),
(N'MIC-EQ-033',N'Storage',N'REFRIGERATED_STORAGE',N'Refrigerated storage of microbiology materials according to approved storage requirements.',N'Instrument List',10),
(N'MIC-EQ-034',N'Storage',N'DEEP_FREEZER_MINUS20',N'Controlled low-temperature storage at -20 C.',N'Instrument List',10);

MERGE dbo.LabEquipmentOperationalUses AS target
USING
(
    SELECT e.EquipmentID,u.UseCategory,u.UseCode,u.UseDescription,u.EvidenceSource,u.SortOrder
    FROM @Uses u
    JOIN dbo.LabEquipment e ON e.EquipmentCode=u.EquipmentCode
) AS source
ON target.EquipmentID=source.EquipmentID
AND target.UseCode=source.UseCode
WHEN MATCHED THEN
    UPDATE SET
        UseCategory=source.UseCategory,
        UseDescription=source.UseDescription,
        EvidenceSource=source.EvidenceSource,
        SortOrder=source.SortOrder,
        IsActive=1
WHEN NOT MATCHED THEN
    INSERT(EquipmentID,UseCategory,UseCode,UseDescription,EvidenceSource,SortOrder,IsActive,CreatedBy)
    VALUES(source.EquipmentID,source.UseCategory,source.UseCode,source.UseDescription,source.EvidenceSource,source.SortOrder,1,N'CONTROLLED-MIGRATION');

-- Additional direct-result compatibility where the test catalog explicitly identifies Air Sampler use.
MERGE dbo.LabEquipmentCompatibilityRules AS target
USING
(
    SELECT N'WATER' AS Module,N'TESTID:23' AS MatchKey,N'Air Sampler' AS EquipmentType,
           N'Total Microbial Count (Air Sampler) requires the controlled Air Sampler.' AS RuleDescription
    UNION ALL
    SELECT N'WATER',N'TESTID:25',N'Air Sampler',
           N'Fungal Count (Air Sampler) requires the controlled Air Sampler.'
) AS source
ON target.Module=source.Module
AND target.MatchKey=source.MatchKey
AND target.EquipmentType=source.EquipmentType
WHEN MATCHED THEN
    UPDATE SET RuleDescription=source.RuleDescription,IsActive=1
WHEN NOT MATCHED THEN
    INSERT(Module,MatchKey,EquipmentType,RuleDescription,IsActive,CreatedBy)
    VALUES(source.Module,source.MatchKey,source.EquipmentType,source.RuleDescription,1,N'CONTROLLED-MIGRATION');

IF EXISTS
(
    SELECT 1
    FROM dbo.LabEquipment e
    WHERE e.EquipmentCode LIKE N'MIC-EQ-%'
      AND NOT EXISTS
      (
          SELECT 1 FROM dbo.LabEquipmentOperationalUses u
          WHERE u.EquipmentID=e.EquipmentID AND u.IsActive=1
      )
)
    THROW 56431, 'One or more imported microbiology equipment records have no controlled operational-use mapping.', 1;

IF (SELECT COUNT(*) FROM dbo.LabEquipment e WHERE e.EquipmentCode LIKE N'MIC-EQ-%') >= 34
   AND (SELECT COUNT(DISTINCT e.EquipmentID)
        FROM dbo.LabEquipment e
        JOIN dbo.LabEquipmentOperationalUses u ON u.EquipmentID=e.EquipmentID AND u.IsActive=1
        WHERE e.EquipmentCode LIKE N'MIC-EQ-%') < 34
    THROW 56432, 'The 34-record microbiology equipment operational-use matrix is incomplete.', 1;
