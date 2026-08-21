using Microsoft.Data.Sqlite;

namespace Meimad.Planner.Server.Persistence;

internal sealed class SchemaV39MaterialReconciliationMigration : IDatabaseMigration
{
    public int Version => 39;

    public string Name => "verified_material_receipts_and_batch_reservations";

    public async Task ApplyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE verified_material_receipts (
                id TEXT PRIMARY KEY,
                case_id TEXT NOT NULL REFERENCES cases(id) ON DELETE RESTRICT,
                quantity INTEGER NOT NULL CHECK (quantity > 0),
                unit TEXT NOT NULL DEFAULT 'piece' CHECK (unit = 'piece'),
                received_at TEXT NOT NULL,
                verified_at TEXT NOT NULL,
                verified_by TEXT NOT NULL,
                external_reference TEXT NULL CHECK (
                    external_reference IS NULL OR length(external_reference) <= 200),
                comment TEXT NULL CHECK (comment IS NULL OR length(comment) <= 2000),
                source TEXT NOT NULL DEFAULT 'LOCAL_VERIFIED'
                    CHECK (source = 'LOCAL_VERIFIED'),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX ix_verified_material_receipts_case_received
                ON verified_material_receipts(case_id, received_at, id);

            CREATE TABLE batch_material_reservations (
                id TEXT PRIMARY KEY,
                receipt_id TEXT NOT NULL
                    REFERENCES verified_material_receipts(id) ON DELETE RESTRICT,
                production_batch_id TEXT NOT NULL
                    REFERENCES production_batches(id) ON DELETE CASCADE,
                quantity INTEGER NOT NULL CHECK (quantity > 0),
                reserved_at TEXT NOT NULL,
                reserved_by TEXT NOT NULL,
                comment TEXT NULL CHECK (comment IS NULL OR length(comment) <= 2000),
                version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(receipt_id, production_batch_id)
            );

            CREATE INDEX ix_batch_material_reservations_batch
                ON batch_material_reservations(production_batch_id, receipt_id);

            CREATE INDEX ix_batch_material_reservations_receipt
                ON batch_material_reservations(receipt_id, production_batch_id);

            CREATE TRIGGER batch_material_reservation_case_match_insert
            BEFORE INSERT ON batch_material_reservations
            FOR EACH ROW
            WHEN NOT EXISTS (
                SELECT 1
                FROM verified_material_receipts receipt
                JOIN production_batches batch ON batch.id = NEW.production_batch_id
                WHERE receipt.id = NEW.receipt_id
                  AND receipt.case_id = batch.case_id)
            BEGIN
                SELECT RAISE(ABORT, 'material receipt and Production Batch must belong to the same Case');
            END;

            CREATE TRIGGER batch_material_reservation_receipt_capacity_insert
            BEFORE INSERT ON batch_material_reservations
            FOR EACH ROW
            WHEN NEW.quantity + COALESCE((
                SELECT SUM(quantity)
                FROM batch_material_reservations
                WHERE receipt_id = NEW.receipt_id), 0) > (
                SELECT quantity FROM verified_material_receipts WHERE id = NEW.receipt_id)
            BEGIN
                SELECT RAISE(ABORT, 'material reservation exceeds verified receipt quantity');
            END;

            CREATE TRIGGER batch_material_reservation_batch_capacity_insert
            BEFORE INSERT ON batch_material_reservations
            FOR EACH ROW
            WHEN NEW.quantity + COALESCE((
                SELECT SUM(quantity)
                FROM batch_material_reservations
                WHERE production_batch_id = NEW.production_batch_id), 0) > (
                SELECT planned_quantity FROM production_batches WHERE id = NEW.production_batch_id)
            BEGIN
                SELECT RAISE(ABORT, 'material reservation exceeds Production Batch planned quantity');
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
