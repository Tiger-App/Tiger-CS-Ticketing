using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerProfileAuditAndProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalPhones",
                table: "CustomerProfileOverrides",
                type: "nvarchar(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "CustomerProfileOverrides",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerProfileId",
                table: "CustomerProfileOverrides",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "CustomerProfileOverrides",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Emirate",
                table: "CustomerProfileOverrides",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FullName",
                table: "CustomerProfileOverrides",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LandlineNumber",
                table: "CustomerProfileOverrides",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Location",
                table: "CustomerProfileOverrides",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Nationality",
                table: "CustomerProfileOverrides",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "CustomerProfileOverrides",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerProfileChanges",
                columns: table => new
                {
                    CustomerProfileChangeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    OldValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    NewValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ModifiedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModifiedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ChangeReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RelatedTicketId = table.Column<long>(type: "bigint", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangeKey = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerProfileChanges", x => x.CustomerProfileChangeId);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPropertyAssociations",
                columns: table => new
                {
                    CustomerPropertyAssociationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    CustomerProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UnitNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Relationship = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPropertyAssociations", x => x.CustomerPropertyAssociationId);
                });

            // Rows that already exist (the table is live in production) each get their own stable identifier BEFORE the unique index is
            // built - without this every existing row would hold the all-zero default and the index could not be created.
            // EXEC defers compilation: the column is added earlier in the same batch of the idempotent script, and T-SQL would otherwise
            // refuse to compile a statement that names a column that does not exist yet.
            migrationBuilder.Sql("EXEC(N'UPDATE [CustomerProfileOverrides] SET [CustomerProfileId] = NEWID() WHERE [CustomerProfileId] = ''00000000-0000-0000-0000-000000000000'';');");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfileOverrides_CustomerProfileId",
                table: "CustomerProfileOverrides",
                column: "CustomerProfileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfileChanges_ChangeKey",
                table: "CustomerProfileChanges",
                column: "ChangeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfileChanges_CorrelationId",
                table: "CustomerProfileChanges",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfileChanges_CustomerKey",
                table: "CustomerProfileChanges",
                column: "CustomerKey");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerProfileChanges_CustomerProfileId_ModifiedAtUtc",
                table: "CustomerProfileChanges",
                columns: new[] { "CustomerProfileId", "ModifiedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPropertyAssociations_CustomerKey",
                table: "CustomerPropertyAssociations",
                column: "CustomerKey");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPropertyAssociations_CustomerProfileId_ProjectName_UnitNumber",
                table: "CustomerPropertyAssociations",
                columns: new[] { "CustomerProfileId", "ProjectName", "UnitNumber" },
                unique: true);

            // Append-only history: the database itself refuses to change or remove an audit row, whoever asks (an application bug, a
            // maintenance script, a privileged login). Dropping the table - the rollback - removes the trigger with it.
            // CREATE TRIGGER must be the first statement of its batch, and the idempotent script wraps every operation in an IF block, so
            // it is created through EXEC.
            migrationBuilder.Sql(
                "EXEC(N'CREATE TRIGGER [TR_CustomerProfileChanges_AppendOnly] ON [CustomerProfileChanges] INSTEAD OF UPDATE, DELETE AS BEGIN SET NOCOUNT ON; " +
                "THROW 51000, ''CustomerProfileChanges is append-only: audit rows cannot be updated or deleted.'', 1; END');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerProfileChanges");

            migrationBuilder.DropTable(
                name: "CustomerPropertyAssociations");

            migrationBuilder.DropIndex(
                name: "IX_CustomerProfileOverrides_CustomerProfileId",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "AdditionalPhones",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "CustomerProfileId",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "Emirate",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "FullName",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "LandlineNumber",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "Nationality",
                table: "CustomerProfileOverrides");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "CustomerProfileOverrides");
        }
    }
}
