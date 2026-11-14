using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Medical.Hospital.Migrations.Sqlite
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
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    VisitId = table.Column<string>(type: "TEXT", nullable: false),
                    SepNumber = table.Column<string>(type: "TEXT", nullable: false),
                    CbgCode = table.Column<string>(type: "TEXT", nullable: true),
                    CbgDescription = table.Column<string>(type: "TEXT", nullable: true),
                    CbgTariff = table.Column<decimal>(type: "TEXT", precision: 18, nullable: false),
                    HospitalTariff = table.Column<decimal>(type: "TEXT", precision: 18, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<int>(type: "INTEGER", nullable: false),
                    CoderNik = table.Column<string>(type: "TEXT", nullable: true),
                    GroupedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    RawResponse = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BpjsClaimRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceRegistration",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    WardId = table.Column<string>(type: "TEXT", nullable: false),
                    TokenPrefix = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EnrolledBy = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRegistration", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Hl7OutboxMessage",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    MessageType = table.Column<string>(type: "TEXT", nullable: false),
                    ControlId = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    Destination = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    VisitId = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Hl7OutboxMessage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SatuSehatSubmissionRecord",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    VisitId = table.Column<string>(type: "TEXT", nullable: false),
                    PatientId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PatientIhs = table.Column<string>(type: "TEXT", nullable: true),
                    EncounterId = table.Column<string>(type: "TEXT", nullable: true),
                    ResourceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Warnings = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    RawResponse = table.Column<string>(type: "TEXT", nullable: true),
                    TenantId = table.Column<string>(type: "TEXT", nullable: false)
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
