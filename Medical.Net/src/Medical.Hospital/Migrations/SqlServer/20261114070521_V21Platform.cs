using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Medical.Hospital.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class V21Platform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BpjsClaimRecord",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    VisitId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SepNumber = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CbgCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CbgDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CbgTariff = table.Column<decimal>(type: "decimal(18,2)", precision: 18, nullable: false),
                    HospitalTariff = table.Column<decimal>(type: "decimal(18,2)", precision: 18, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Completed = table.Column<int>(type: "int", nullable: false),
                    CoderNik = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GroupedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BpjsClaimRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRegistration",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DeviceId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WardId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokenPrefix = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EnrolledBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRegistration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Hl7OutboxMessage",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    MessageType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ControlId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Destination = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    VisitId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hl7OutboxMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SatuSehatSubmissionRecord",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    VisitId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PatientId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PatientIhs = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EncounterId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResourceCount = table.Column<int>(type: "int", nullable: false),
                    Warnings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SatuSehatSubmissionRecord", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BpjsClaimRecord_TenantId",
                table: "BpjsClaimRecord",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRegistration_TenantId",
                table: "DeviceRegistration",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Hl7OutboxMessage_TenantId",
                table: "Hl7OutboxMessage",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SatuSehatSubmissionRecord_TenantId",
                table: "SatuSehatSubmissionRecord",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BpjsClaimRecord");

            migrationBuilder.DropTable(
                name: "DeviceRegistration");

            migrationBuilder.DropTable(
                name: "Hl7OutboxMessage");

            migrationBuilder.DropTable(
                name: "SatuSehatSubmissionRecord");
        }
    }
}
