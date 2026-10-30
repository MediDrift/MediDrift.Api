using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediDrift.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServerSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TerminologyCacheEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IdentifierType = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    Identifier = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ConceptName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CanonicalRxcui = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CandidatesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReleaseStartDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReleaseEndDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RxNormVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FetchedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminologyCacheEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    FileExtension = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    WorksheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    MappingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AppliedProfileName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ImportedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportJobs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MappingProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    MappingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MappingProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MappingProfiles_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportColumns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ImportJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    SuggestedTargetField = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuggestionConfidence = table.Column<double>(type: "float", nullable: false),
                    NeedsReview = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportColumns_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MedicationAuditRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ImportJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    TotalRecords = table.Column<int>(type: "int", nullable: false),
                    TotalIdentifiers = table.Column<int>(type: "int", nullable: false),
                    ProcessedIdentifiers = table.Column<int>(type: "int", nullable: false),
                    ActiveCount = table.Column<int>(type: "int", nullable: false),
                    ObsoleteCount = table.Column<int>(type: "int", nullable: false),
                    RemappedCount = table.Column<int>(type: "int", nullable: false),
                    QuantifiedCount = table.Column<int>(type: "int", nullable: false),
                    NotCurrentCount = table.Column<int>(type: "int", nullable: false),
                    UnknownCount = table.Column<int>(type: "int", nullable: false),
                    AlienCount = table.Column<int>(type: "int", nullable: false),
                    InvalidFormatCount = table.Column<int>(type: "int", nullable: false),
                    MissingIdentifierCount = table.Column<int>(type: "int", nullable: false),
                    LookupErrorCount = table.Column<int>(type: "int", nullable: false),
                    RxNormVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationAuditRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MedicationAuditRuns_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MedicationAuditRuns_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "MedicationRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ImportJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: true),
                    MedicationName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RxCUI = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    NDC = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Strength = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    DosageForm = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    BrandName = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    OriginalValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MedicationRecords_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StagedImportRows",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ImportJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    ValuesJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StagedImportRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StagedImportRows_ImportJobs_ImportJobId",
                        column: x => x.ImportJobId,
                        principalTable: "ImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MedicationAuditFindings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuditRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MedicationRecordId = table.Column<long>(type: "bigint", nullable: false),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: true),
                    MedicationName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IdentifierType = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    SubmittedIdentifier = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    ConceptName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CanonicalRxcui = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    CandidatesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReleaseStartDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReleaseEndDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicationAuditFindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MedicationAuditFindings_MedicationAuditRuns_AuditRunId",
                        column: x => x.AuditRunId,
                        principalTable: "MedicationAuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MedicationAuditFindings_MedicationRecords_MedicationRecordId",
                        column: x => x.MedicationRecordId,
                        principalTable: "MedicationRecords",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportColumns_ImportJobId_Position",
                table: "ImportColumns",
                columns: new[] { "ImportJobId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportJobs_WorkspaceId_CreatedUtc",
                table: "ImportJobs",
                columns: new[] { "WorkspaceId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MappingProfiles_WorkspaceId_Name",
                table: "MappingProfiles",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MedicationAuditFindings_AuditRunId_SourceRowNumber",
                table: "MedicationAuditFindings",
                columns: new[] { "AuditRunId", "SourceRowNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_MedicationAuditFindings_AuditRunId_Status",
                table: "MedicationAuditFindings",
                columns: new[] { "AuditRunId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MedicationAuditFindings_MedicationRecordId",
                table: "MedicationAuditFindings",
                column: "MedicationRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationAuditRuns_ImportJobId",
                table: "MedicationAuditRuns",
                column: "ImportJobId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicationAuditRuns_WorkspaceId_ImportJobId_CreatedUtc",
                table: "MedicationAuditRuns",
                columns: new[] { "WorkspaceId", "ImportJobId", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MedicationRecords_ImportJobId_SourceRowNumber",
                table: "MedicationRecords",
                columns: new[] { "ImportJobId", "SourceRowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StagedImportRows_ImportJobId_RowNumber",
                table: "StagedImportRows",
                columns: new[] { "ImportJobId", "RowNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TerminologyCacheEntries_IdentifierType_Identifier",
                table: "TerminologyCacheEntries",
                columns: new[] { "IdentifierType", "Identifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportColumns");

            migrationBuilder.DropTable(
                name: "MappingProfiles");

            migrationBuilder.DropTable(
                name: "MedicationAuditFindings");

            migrationBuilder.DropTable(
                name: "StagedImportRows");

            migrationBuilder.DropTable(
                name: "TerminologyCacheEntries");

            migrationBuilder.DropTable(
                name: "MedicationAuditRuns");

            migrationBuilder.DropTable(
                name: "MedicationRecords");

            migrationBuilder.DropTable(
                name: "ImportJobs");

            migrationBuilder.DropTable(
                name: "Workspaces");
        }
    }
}
