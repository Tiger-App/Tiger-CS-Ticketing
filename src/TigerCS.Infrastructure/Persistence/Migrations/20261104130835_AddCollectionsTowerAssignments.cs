using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TigerCS.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionsTowerAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CollectionsTowerAssignmentHistory",
                columns: table => new
                {
                    CollectionsTowerAssignmentHistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TowerId = table.Column<int>(type: "int", nullable: false),
                    TowerCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TowerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    PreviousEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Action = table.Column<byte>(type: "tinyint", nullable: false),
                    ChangedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsTowerAssignmentHistory", x => x.CollectionsTowerAssignmentHistoryId);
                    table.ForeignKey(
                        name: "FK_CollectionsTowerAssignmentHistory_Employees_ChangedByEmployeeId",
                        column: x => x.ChangedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CollectionsTowerAssignmentHistory_Employees_NewEmployeeId",
                        column: x => x.NewEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CollectionsTowerAssignmentHistory_Employees_PreviousEmployeeId",
                        column: x => x.PreviousEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectionsTowerAssignments",
                columns: table => new
                {
                    CollectionsTowerAssignmentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TowerId = table.Column<int>(type: "int", nullable: false),
                    TowerCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TowerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CompanyId = table.Column<int>(type: "int", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActivatedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionsTowerAssignments", x => x.CollectionsTowerAssignmentId);
                    table.ForeignKey(
                        name: "FK_CollectionsTowerAssignments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketAssignmentHistory",
                columns: table => new
                {
                    TicketAssignmentHistoryEntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    PreviousEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    NewEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<byte>(type: "tinyint", nullable: false),
                    TowerId = table.Column<int>(type: "int", nullable: true),
                    TowerCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    TowerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UnitNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ChangedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketAssignmentHistory", x => x.TicketAssignmentHistoryEntryId);
                    table.ForeignKey(
                        name: "FK_TicketAssignmentHistory_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "DepartmentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignmentHistory_Employees_ChangedByEmployeeId",
                        column: x => x.ChangedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignmentHistory_Employees_NewEmployeeId",
                        column: x => x.NewEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignmentHistory_Employees_PreviousEmployeeId",
                        column: x => x.PreviousEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketAssignmentHistory_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketCollectionsUnits",
                columns: table => new
                {
                    TicketId = table.Column<long>(type: "bigint", nullable: false),
                    TowerId = table.Column<int>(type: "int", nullable: false),
                    TowerCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TowerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CompanyId = table.Column<int>(type: "int", nullable: false),
                    UnitRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    UnitNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SelectedByEmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SelectedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketCollectionsUnits", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_TicketCollectionsUnits_Employees_SelectedByEmployeeId",
                        column: x => x.SelectedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "EmployeeId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TicketCollectionsUnits_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "TicketId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsTowerAssignmentHistory_ChangedByEmployeeId",
                table: "CollectionsTowerAssignmentHistory",
                column: "ChangedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsTowerAssignmentHistory_Month_Tower",
                table: "CollectionsTowerAssignmentHistory",
                columns: new[] { "Year", "Month", "TowerId" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsTowerAssignmentHistory_NewEmployeeId",
                table: "CollectionsTowerAssignmentHistory",
                column: "NewEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsTowerAssignmentHistory_PreviousEmployeeId",
                table: "CollectionsTowerAssignmentHistory",
                column: "PreviousEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsTowerAssignments_EmployeeId",
                table: "CollectionsTowerAssignments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionsTowerAssignments_Month_Employee",
                table: "CollectionsTowerAssignments",
                columns: new[] { "Year", "Month", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "UX_CollectionsTowerAssignments_Tower_Month_Primary",
                table: "CollectionsTowerAssignments",
                columns: new[] { "TowerId", "Year", "Month" },
                unique: true,
                filter: "[IsPrimary] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignmentHistory_ChangedByEmployeeId",
                table: "TicketAssignmentHistory",
                column: "ChangedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignmentHistory_DepartmentId",
                table: "TicketAssignmentHistory",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignmentHistory_NewEmployeeId",
                table: "TicketAssignmentHistory",
                column: "NewEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignmentHistory_PreviousEmployeeId",
                table: "TicketAssignmentHistory",
                column: "PreviousEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketAssignmentHistory_Ticket_Changed",
                table: "TicketAssignmentHistory",
                columns: new[] { "TicketId", "ChangedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketCollectionsUnits_SelectedByEmployeeId",
                table: "TicketCollectionsUnits",
                column: "SelectedByEmployeeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CollectionsTowerAssignmentHistory");

            migrationBuilder.DropTable(
                name: "CollectionsTowerAssignments");

            migrationBuilder.DropTable(
                name: "TicketAssignmentHistory");

            migrationBuilder.DropTable(
                name: "TicketCollectionsUnits");
        }
    }
}
