namespace PharmaLIMS.Services;
internal static class ReviewClosureSchemaContract
{
    internal const string Sql=@"SELECT N'Culture identity trigger '+expected.TriggerName+N' is missing, disabled, attached elsewhere or has no protective body.' AS Details
FROM (VALUES(N'CultureMedia',N'TRG_CultureMedia_ReferencedIdentity',N'Referenced culture media identity is frozen'),
(N'CultureMediaLots',N'TRG_CultureMediaLots_ReferencedIdentity',N'Qualified/prepared culture media lot identity is frozen')) expected(TableName,TriggerName,BodyToken)
LEFT JOIN sys.triggers t ON t.object_id=OBJECT_ID(N'dbo.'+expected.TriggerName,N'TR')
WHERE t.object_id IS NULL OR t.parent_id<>OBJECT_ID(N'dbo.'+expected.TableName) OR t.is_disabled=1 OR t.is_instead_of_trigger=1
OR CHARINDEX(expected.BodyToken,OBJECT_DEFINITION(t.object_id))=0
OR (SELECT COUNT(*) FROM sys.trigger_events ev WHERE ev.object_id=t.object_id AND ev.type_desc IN(N'UPDATE',N'DELETE'))<>2
UNION ALL SELECT N'Media preparation workflow rowversion is missing or invalid.'
WHERE NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.MediaPreparations') AND name=N'WorkflowRowVersion' AND system_type_id=189 AND max_length=8)
UNION ALL SELECT N'Water resource execution date is missing or invalid.'
WHERE NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.WaterResultResourceEvidence') AND name=N'ExecutionDate' AND system_type_id=40 AND max_length=3)
UNION ALL SELECT N'PRM specification content SHA-256 column is missing or invalid.'
WHERE NOT EXISTS(SELECT 1 FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationContentHistory') AND name=N'ContentHash' AND system_type_id=173 AND max_length=32 AND is_nullable=0)
UNION ALL SELECT N'PRM content history signature foreign key is missing or disabled.'
WHERE NOT EXISTS(SELECT 1 FROM sys.foreign_keys fk JOIN sys.foreign_key_columns fc ON fc.constraint_object_id=fk.object_id
WHERE fk.parent_object_id=OBJECT_ID(N'dbo.PRM_SpecificationContentHistory') AND fk.referenced_object_id=OBJECT_ID(N'dbo.PRM_SpecificationSignatures') AND fk.is_disabled=0 AND fk.is_not_trusted=0
AND COL_NAME(fc.parent_object_id,fc.parent_column_id)=N'SignatureID' AND COL_NAME(fc.referenced_object_id,fc.referenced_column_id)=N'SignatureID')
UNION ALL SELECT N'PRM content history signature uniqueness is missing or disabled.'
WHERE NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.PRM_SpecificationContentHistory') AND name=N'UX_PRM_SpecificationContentHistory_Signature' AND is_unique=1 AND is_disabled=0 AND has_filter=1);";
}
