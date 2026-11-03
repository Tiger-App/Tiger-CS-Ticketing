/*
================================================================================
 Optional_TigerCS_PROD_AuditEntries_EntityIndex.sql      (OPTIONAL - NOT part of the release - run only if the Activity tab is slow)
 Database : TigerCS_Ticketing_Prod

 The Activity timeline reads AuditEntries by (EntityType, EntityId) for one ticket at a time. AuditEntries has no index on those columns, so each
 view scans the table. On a small table that is fine. If AuditEntries has grown large and the Activity tab becomes slow, this adds a NON-CLUSTERED,
 ONLINE-capable index (no data change, easily dropped). It is idempotent. It is deliberately NOT an EF migration, so the release needs no schema step.
 Run in a quiet period; ONLINE = ON needs Enterprise/Azure SQL - remove that option on Standard.
================================================================================
*/
SET NOCOUNT ON;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditEntries_EntityType_EntityId_Action' AND object_id = OBJECT_ID(N'dbo.AuditEntries'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_AuditEntries_EntityType_EntityId_Action
        ON dbo.AuditEntries (EntityType, EntityId, Action)
        INCLUDE (OccurredAtUtc, CorrelationId, ActorEmployeeId)
        WITH (ONLINE = ON);
    PRINT 'Created IX_AuditEntries_EntityType_EntityId_Action.';
END
ELSE PRINT 'IX_AuditEntries_EntityType_EntityId_Action already exists - nothing to do.';
-- Rollback: DROP INDEX IX_AuditEntries_EntityType_EntityId_Action ON dbo.AuditEntries;
