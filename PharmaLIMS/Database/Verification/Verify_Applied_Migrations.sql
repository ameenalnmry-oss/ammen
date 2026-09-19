/* PharmaLIMS migration deployment verification - Batch 15 */
SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.LIMS_SchemaVersions', N'U') IS NULL
BEGIN
    SELECT N'LIMS_SchemaVersions table is not present; verify migrations using the controlled deployment log.' AS VerificationMessage;
END
ELSE
BEGIN
    DECLARE @Expected TABLE
    (
        VersionKey NVARCHAR(100) NOT NULL PRIMARY KEY,
        MigrationChecksum NVARCHAR(128) NOT NULL
    );

    INSERT @Expected(VersionKey,MigrationChecksum) VALUES
    (N'20260712_001',N'b5c51b277e4a1bfe5f8de8ab97eb295ddb29d6871e5c3a7c2035251ce656589a'),
    (N'20260714_001',N'9dcf22a5ac212f567a7db1ca50904d35d7cf4fe11b90f7edf05e5449051d5e94'),
    (N'20260715_001',N'97c58509a872bacb91f7c6e7964185e6b846a1df780ef609e0686baeb9b377f9'),
    (N'20260716_002',N'53b183e0596839d977f847e9db6ca20cdfd0c23856e3f18b79e1a59f07d678ac'),
    (N'20260716_003',N'31255580e6c87f85ad468c2303bab1d8b6157afbc079457ba43bffc9075ff61e'),
    (N'20260717_001',N'2251af5d3682b9831af568630368a22c7d5e0b9888bbc48835dc9eac64b84cba'),
    (N'20260717_002',N'5595bb418009a66789cdea53dafc777b2ebedd63bffcf4183bd6dbed146312b9'),
    (N'20260717_003',N'b547257a1106f1afeb8aaf1b99a7261dfb687702bb52dec3bff28756a7ebe8af'),
    (N'20260718_001',N'0a56913e9cd28fa906ce8520a68000cac24fab0a8f33023e3ceab8a1acd2f9a2'),
    (N'20260718_002',N'59aba08ab311b0931b019652c163a963d62749f1459d4f54427f65596f3bbe2c'),
    (N'20260721_001',N'0d7fb57c000b8c038e6d5f85bf8ded83d915b288240653b669bf5b57edfeeb10'),
    (N'20260721_002',N'2c5c7a7da5c89b2bb7bfd05ed54e969f2c747e9480a7144de1cfb4522f68f85e'),
    (N'20260722_001',N'5c82f3db9cf356d64d30e03d8be2f49ca9a002e50a0a93f5f43982dd5bec3f3b'),
    (N'20260722_002',N'e6985d446a1d7919a6ba3bf1a09a1ab7309fbd5adb24033f5fc5c3596eb635ec'),
    (N'20260722_003',N'47fb20228e02d4ab57dd0bb30847d2a6466322f1850fb311e64c7739e7bc4f11'),
    (N'20260722_004',N'387e4eb4e726a540153cf3939a3e821a191d8fc29ab5472eb02fee6c8592684c'),
    (N'20260722_005',N'a77c01e996c71b9db880d12a1332bfacb66d9a2b4463705c261657e7a45d0b25'),
    (N'20260723_002R2',N'80e9654dfa10d79260d3a32a40612c1c107f1a197618ac16edb811c9e0f86ed4');

    IF EXISTS
    (
        SELECT 1
        FROM @Expected e
        LEFT JOIN dbo.LIMS_SchemaVersions v ON v.VersionKey=e.VersionKey
        WHERE v.VersionKey IS NULL OR LOWER(ISNULL(v.MigrationChecksum,N''))<>LOWER(e.MigrationChecksum)
    )
        THROW 52001, 'One or more controlled migrations are missing or have a checksum mismatch.', 1;

    SELECT VersionKey, Description, AppliedAt, AppliedBy, MigrationChecksum, ApplicationVersion
    FROM dbo.LIMS_SchemaVersions
    ORDER BY AppliedAt, VersionKey;
END;
