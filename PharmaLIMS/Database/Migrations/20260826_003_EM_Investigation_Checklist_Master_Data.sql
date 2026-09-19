/*
    PharmaLIMS 2026.8.26.166
    Controlled Environmental Monitoring investigation checklist master-data synchronization.

    Purpose:
      - move EM checklist synchronization into the checksum-controlled migration ledger;
      - preserve existing QuestionID values and saved investigation answers;
      - keep Database Maintenance restartable and free from ad-hoc runtime DML loops.

    This migration updates controlled checklist definitions only. It does not update
    QualityEvents, checklist answers, result records, signatures, or audit evidence.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'dbo.QualityEventChecklistQuestions',N'U') IS NULL
    THROW 53800, 'Quality Event checklist master table is missing. Apply prerequisite controlled migrations first.', 1;

IF COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'ExpectedAnswer') IS NULL
   OR COL_LENGTH(N'dbo.QualityEventChecklistQuestions',N'QuestionLogic') IS NULL
    THROW 53801, 'Quality Event checklist control columns are missing. Apply prerequisite controlled migrations first.', 1;

DECLARE @Questions TABLE
(
    SectionName NVARCHAR(200) NOT NULL,
    QuestionText NVARCHAR(MAX) NOT NULL,
    Keyword NVARCHAR(200) NOT NULL,
    SortOrder INT NOT NULL PRIMARY KEY
);

INSERT @Questions(SectionName,QuestionText,Keyword,SortOrder) VALUES
(N'Phase I - Laboratory Review',N'Was analyst training and authorization verified for the EM sampling/testing activity?',N'All',7900),
(N'Phase I - Laboratory Review',N'Was an analyst/sampler interview documented and did it identify no relevant abnormality or error?',N'All',7910),
(N'Phase I - Laboratory Review',N'Were calculation, transcription, and result-entry checks independently reviewed with no attributable error?',N'All',7920),
(N'Phase I - Laboratory Review',N'Was the current approved method/SOP verified and followed for sampling, incubation, reading, and calculation?',N'All',7930),
(N'Phase I - Laboratory Review',N'Were acceptance criteria, calculations, and alert/action limit interpretations independently verified?',N'All',7940),
(N'Phase I - Laboratory Review',N'Were sample/plate identity and chain of custody fully traceable from collection through reading?',N'All',7950),
(N'Phase I - Laboratory Review',N'Were sampling, transport, holding time, and handling practices reviewed with no attributable error?',N'All',7960),
(N'Phase I - Laboratory Review',N'Were raw data and audit trail reviewed with no unexplained deletion, modification, repetition, or backdating?',N'All',7970),
(N'Phase I - Laboratory Review',N'Were applicable equipment/instrument status, calibration, incubator status, and controls reviewed and acceptable?',N'All',7980),
(N'EM Sampling',N'Was the EM sampling location/point correct according to the approved EM plan?',N'All',8010),
(N'EM Sampling',N'Was sampling performed during the documented activity condition?',N'All',8020),
(N'EM Sampling',N'Were sampling date, time, duration or sampled volume, and point/plate identity fully traceable?',N'All',8030),
(N'EM Media',N'Was media lot number recorded, within expiry, and released for use?',N'All',8040),
(N'EM Media',N'Was Growth Promotion Test status acceptable for the used media lot?',N'All',8050),
(N'EM Traceability',N'Were plate/sample labels, event number, area, grade, and monitoring method traceable without discrepancy?',N'All',8060),
(N'EM Handling',N'Were transport, holding time, and handling conditions acceptable before incubation or reading?',N'All',8070),
(N'EM Incubation',N'Were incubation time and temperature within the approved procedure?',N'All',8080),
(N'EM Controls',N'Were negative controls and other applicable controls acceptable?',N'All',8090),
(N'EM Result Review',N'Was colony count/result interpretation independently verified against approved alert/action limits?',N'All',8100),
(N'EM Identification',N'For an action-level excursion or significant recovery, was organism identification completed or formally initiated as required by procedure?',N'All',8110),
(N'EM Identification',N'Was the identified organism or morphology reviewed against historical flora, objectionable-organism risk, and likely contamination source?',N'All',8120),
(N'Cleaning / Disinfection',N'Were cleaning and disinfection records for the relevant area and period reviewed and found acceptable?',N'All',8130),
(N'Cleaning / Disinfection',N'Were disinfectant preparation/concentration, rotation, contact time, and sanitization execution reviewed for the relevant period?',N'All',8140),
(N'HVAC / Facility',N'Were relevant pressure differentials, temperature/humidity, HVAC alarms or excursions, and facility conditions reviewed?',N'All',8150),
(N'Area / Personnel',N'Were doors, personnel movement, cleaning/sanitization, and area activity normal during sampling?',N'All',8160),
(N'Personnel',N'Where personnel could contribute, were training, gowning qualification, aseptic behavior, and personnel monitoring history reviewed?',N'All',8170),
(N'Trend Review',N'Were adjacent/related monitoring points and previous/subsequent EM results reviewed for the same area and grade?',N'All',8180),
(N'Trend Review',N'Was recurrence or an adverse microbiological trend assessed using the available historical EM data?',N'All',8190),
(N'Impact Assessment',N'Was potential impact on exposed product, material, batch, process, and area state of control assessed and documented?',N'All',8200),
(N'Related Records',N'Were related deviations, maintenance, cleaning events, alarms, previous Quality Events, and CAPA records reviewed?',N'All',8210),
(N'Follow-up Monitoring',N'Was the follow-up monitoring/resampling strategy scientifically justified, with locations, timing, acceptance criteria, and interpretation defined?',N'All',8220),
(N'CAPA',N'Was the CAPA requirement scientifically justified based on root cause, recurrence risk, and impact assessment?',N'All',8230),
(N'QA Disposition',N'Was QA disposition documented before final approval/report issuance?',N'All',8240),
(N'QA Disposition',N'Was final QA disposition scientifically justified with the area status, product/material impact, and follow-up requirements clearly documented?',N'All',8245),
(N'Settle Plate',N'Was settle plate exposure time within the approved procedure?',N'settle',8250),
(N'Settle Plate',N'Was plate handling performed aseptically and protected from accidental contamination?',N'settle',8260),
(N'Active Air Sampling',N'Was air sampler ID recorded and calibration status valid at the time of sampling?',N'active air',8270),
(N'Active Air Sampling',N'Was the sampled air volume correct and used correctly in CFU/m3 calculation?',N'active air',8280),
(N'Contact Plate',N'Were contact-plate site, contact area/time, pressure, and neutralizer suitability appropriate for the sampled surface?',N'contact plate',8290),
(N'Surface Swab',N'Were swab location/area, swab kit and diluent lots, recovery volume, and recovery technique documented and acceptable?',N'surface swab',8300),
(N'Personnel Monitoring',N'Were personnel identity, gowning stage, sampled location, sampling time, and relation to the activity fully documented?',N'personnel',8310),
(N'Root Cause / Fishbone',N'Was Personnel / Training assessed as a potential cause, with evidence documented?',N'All',8320),
(N'Root Cause / Fishbone',N'Was Method / Procedure assessed as a potential cause, with evidence documented?',N'All',8330),
(N'Root Cause / Fishbone',N'Was Equipment / Instrument assessed as a potential cause, with evidence documented?',N'All',8340),
(N'Root Cause / Fishbone',N'Was Material / Media assessed as a potential cause, with evidence documented?',N'All',8350),
(N'Root Cause / Fishbone',N'Was Environment / Facility assessed as a potential cause, with evidence documented?',N'All',8360),
(N'Root Cause / Fishbone',N'Was Measurement / Data assessed as a potential cause, with evidence documented?',N'All',8370);

UPDATE existing
SET existing.SectionName=source.SectionName,
    existing.QuestionText=source.QuestionText,
    existing.AppliesToEventType=N'All',
    existing.AppliesToSampleType=N'All',
    existing.AppliesToTestCategory=N'Environmental Monitoring',
    existing.AppliesToTestNameKeyword=source.Keyword,
    existing.AnswerType=N'YesNoNA',
    existing.IsRequired=1,
    existing.ExpectedAnswer=N'Yes',
    existing.QuestionLogic=N'PositiveCheck',
    existing.SortOrder=source.SortOrder,
    existing.IsActive=1
FROM dbo.QualityEventChecklistQuestions existing
INNER JOIN @Questions source
    ON source.QuestionText=existing.QuestionText
    OR (ISNULL(existing.AppliesToTestCategory,N'')=N'Environmental Monitoring'
        AND existing.SortOrder=source.SortOrder);

INSERT dbo.QualityEventChecklistQuestions
(
    SectionName,QuestionText,AppliesToEventType,AppliesToSampleType,
    AppliesToTestCategory,AppliesToTestNameKeyword,AnswerType,IsRequired,
    SortOrder,IsActive,ExpectedAnswer,QuestionLogic
)
SELECT
    source.SectionName,source.QuestionText,N'All',N'All',
    N'Environmental Monitoring',source.Keyword,N'YesNoNA',1,
    source.SortOrder,1,N'Yes',N'PositiveCheck'
FROM @Questions source
WHERE NOT EXISTS
(
    SELECT 1
    FROM dbo.QualityEventChecklistQuestions existing
    WHERE existing.QuestionText=source.QuestionText
       OR (ISNULL(existing.AppliesToTestCategory,N'')=N'Environmental Monitoring'
           AND existing.SortOrder=source.SortOrder)
);

IF EXISTS
(
    SELECT source.SortOrder
    FROM @Questions source
    WHERE NOT EXISTS
    (
        SELECT 1
        FROM dbo.QualityEventChecklistQuestions q
        WHERE q.SortOrder=source.SortOrder
          AND ISNULL(q.IsActive,1)=1
          AND ISNULL(q.IsRequired,0)=1
          AND ISNULL(q.AppliesToTestCategory,N'')=N'Environmental Monitoring'
          AND ISNULL(q.ExpectedAnswer,N'')=N'Yes'
          AND ISNULL(q.QuestionLogic,N'')=N'PositiveCheck'
    )
)
    THROW 53802, 'Environmental Monitoring investigation checklist synchronization did not reach the controlled definition set.', 1;
