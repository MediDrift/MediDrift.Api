using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using MediDrift.Api.Data;
using MediDrift.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace MediDrift.Api.Endpoints;

public static class MedicationAuditEndpoints
{
    public static IEndpointRouteBuilder MapMedicationAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var imports = endpoints.MapGroup("/api/imports/{importId:guid}/audits").WithTags("Medication audits");
        imports.MapPost("", StartAuditAsync).WithSummary("Queue a terminology audit for an imported catalogue");
        imports.MapGet("latest", GetLatestAuditAsync).WithSummary("Get the latest terminology audit for an import");

        var audits = endpoints.MapGroup("/api/audits").WithTags("Medication audits");
        audits.MapGet("/{auditId:guid}", GetAuditAsync).WithSummary("Get audit status and summary");
        audits.MapPost("/{auditId:guid}/pause", PauseAuditAsync).WithSummary("Pause a running terminology audit");
        audits.MapPost("/{auditId:guid}/resume", ResumeAuditAsync).WithSummary("Resume a paused terminology audit");
        audits.MapPost("/{auditId:guid}/cancel", CancelAuditAsync).WithSummary("Cancel a queued or running terminology audit");
        audits.MapGet("/{auditId:guid}/findings", GetFindingsAsync).WithSummary("Get paginated audit findings");
        audits.MapGet("/{auditId:guid}/export", ExportFindingsAsync).WithSummary("Export audit findings as CSV or XLSX");
        return endpoints;
    }

    private static async Task<IResult> StartAuditAsync(
        Guid importId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var import = await db.ImportJobs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == importId && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (import is null) return Results.NotFound(new ApiError("Import was not found."));
        if (import.Status != ImportStatuses.Imported)
            return Results.Conflict(new ApiError("Confirm the medication import before running an audit."));

        var totalRecords = await db.MedicationRecords.CountAsync(x => x.ImportJobId == importId, cancellationToken);
        if (totalRecords == 0) return Results.BadRequest(new ApiError("This import contains no normalized medication records."));

        var run = new MedicationAuditRun
        {
            Id = Guid.NewGuid(),
            WorkspaceId = DefaultWorkspace.Id,
            ImportJobId = importId,
            Status = MedicationAuditStatuses.Queued,
            TotalRecords = totalRecords,
            CreatedUtc = DateTime.UtcNow
        };
        db.MedicationAuditRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Accepted($"/api/audits/{run.Id}", new MedicationAuditStartedDto(
            ToDto(run), "The terminology audit has been queued. You can monitor its progress here."));
    }

    private static async Task<IResult> GetLatestAuditAsync(Guid importId, AppDbContext db, CancellationToken cancellationToken)
    {
        var importExists = await db.ImportJobs.AsNoTracking().AnyAsync(
            x => x.Id == importId && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (!importExists) return Results.NotFound(new ApiError("Import was not found."));

        var run = await db.MedicationAuditRuns.AsNoTracking()
            .Where(x => x.ImportJobId == importId && x.WorkspaceId == DefaultWorkspace.Id)
            .OrderByDescending(x => x.CreatedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? Results.NoContent() : Results.Ok(ToDto(run));
    }

    private static async Task<IResult> GetAuditAsync(Guid auditId, AppDbContext db, CancellationToken cancellationToken)
    {
        var run = await db.MedicationAuditRuns.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == auditId && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        return run is null ? Results.NotFound(new ApiError("Audit was not found.")) : Results.Ok(ToDto(run));
    }

    private static async Task<IResult> PauseAuditAsync(
        Guid auditId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var run = await db.MedicationAuditRuns.FirstOrDefaultAsync(
            x => x.Id == auditId && x.WorkspaceId == DefaultWorkspace.Id,
            cancellationToken);

        if (run is null) return Results.NotFound(new ApiError("Audit was not found."));
        if (run.Status == MedicationAuditStatuses.Queued)
        {
            // If the worker has not claimed it yet, pausing is immediate. The worker
            // uses a conditional Queued -> Running update and cannot overwrite this.
            run.Status = MedicationAuditStatuses.Paused;
        }
        else if (run.Status == MedicationAuditStatuses.Running)
        {
            // Let the active worker finish its current bounded checkpoint, persist it,
            // then transition to Paused. The UI continues polling until it is safe.
            run.Status = MedicationAuditStatuses.Pausing;
        }
        else
        {
            return Results.Conflict(new ApiError("Only a queued or running audit can be paused."));
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToDto(run));
    }

    private static async Task<IResult> ResumeAuditAsync(
        Guid auditId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var run = await db.MedicationAuditRuns.FirstOrDefaultAsync(
            x => x.Id == auditId && x.WorkspaceId == DefaultWorkspace.Id,
            cancellationToken);

        if (run is null) return Results.NotFound(new ApiError("Audit was not found."));
        if (run.Status != MedicationAuditStatuses.Paused)
            return Results.Conflict(new ApiError("Only a paused audit can be resumed."));

        // The worker reuses persisted findings and terminology cache entries.
        run.Status = MedicationAuditStatuses.Queued;
        run.ErrorMessage = null;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToDto(run));
    }

    private static async Task<IResult> CancelAuditAsync(
        Guid auditId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var run = await db.MedicationAuditRuns.FirstOrDefaultAsync(
            x => x.Id == auditId && x.WorkspaceId == DefaultWorkspace.Id,
            cancellationToken);

        if (run is null) return Results.NotFound(new ApiError("Audit was not found."));

        if (run.Status is MedicationAuditStatuses.Running or MedicationAuditStatuses.Pausing)
        {
            // The active worker saves the last in-flight batch before becoming Cancelled.
            run.Status = MedicationAuditStatuses.Cancelling;
            run.ErrorMessage = "Cancellation requested. Saving completed findings…";
        }
        else if (run.Status is MedicationAuditStatuses.Queued or MedicationAuditStatuses.Paused)
        {
            run.Status = MedicationAuditStatuses.Cancelled;
            run.CompletedUtc = DateTime.UtcNow;
            run.ErrorMessage = "Cancelled by the user. Partial findings have been retained.";
        }
        else if (run.Status != MedicationAuditStatuses.Cancelling)
        {
            return Results.Conflict(new ApiError("This audit can no longer be cancelled."));
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToDto(run));
    }

    private static async Task<IResult> GetFindingsAsync(
        Guid auditId,
        int page,
        int pageSize,
        string? status,
        string? identifierType,
        AppDbContext db,
        CancellationToken cancellationToken,
        bool latest = false)
    {
        var exists = await db.MedicationAuditRuns.AsNoTracking().AnyAsync(
            x => x.Id == auditId && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (!exists) return Results.NotFound(new ApiError("Audit was not found."));

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
        var query = db.MedicationAuditFindings.AsNoTracking().Where(x => x.AuditRunId == auditId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(identifierType)) query = query.Where(x => x.IdentifierType == identifierType);

        var total = await query.CountAsync(cancellationToken);
        var findings = latest
            ? await query.OrderByDescending(x => x.CheckedUtc).ThenByDescending(x => x.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken)
            : await query.OrderBy(x => x.SourceRowNumber).ThenBy(x => x.IdentifierType)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

        return Results.Ok(new PagedResult<MedicationAuditFindingDto>(findings.Select(ToDto).ToArray(), page, pageSize, total));
    }

    private static async Task<IResult> ExportFindingsAsync(
        Guid auditId,
        HttpRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var exists = await db.MedicationAuditRuns.AsNoTracking().AnyAsync(
            x => x.Id == auditId && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (!exists) return Results.NotFound(new ApiError("Audit was not found."));

        var findings = await db.MedicationAuditFindings.AsNoTracking()
            .Where(x => x.AuditRunId == auditId)
            .OrderBy(x => x.SourceRowNumber).ThenBy(x => x.IdentifierType)
            .ToListAsync(cancellationToken);
        var format = request.Query["format"].ToString().ToLowerInvariant();
        if (format == "xlsx")
        {
            using var workbook = new XLWorkbook();
            var sheet = workbook.Worksheets.Add("Audit Findings");
            var headers = new[] { "SourceRowNumber", "ExternalId", "MedicationName", "IdentifierType", "SubmittedIdentifier", "Status", "ConceptName", "CanonicalRxcui", "Candidates", "ReleaseStartDate", "ReleaseEndDate", "Detail", "CheckedUtc" };
            for (var column = 0; column < headers.Length; column++) sheet.Cell(1, column + 1).Value = headers[column];
            for (var index = 0; index < findings.Count; index++)
            {
                var item = findings[index];
                var row = index + 2;
                sheet.Cell(row, 1).Value = item.SourceRowNumber;
                sheet.Cell(row, 2).Value = item.ExternalId ?? string.Empty;
                sheet.Cell(row, 3).Value = item.MedicationName ?? string.Empty;
                sheet.Cell(row, 4).Value = item.IdentifierType;
                sheet.Cell(row, 5).Value = item.SubmittedIdentifier ?? string.Empty;
                sheet.Cell(row, 6).Value = item.Status;
                sheet.Cell(row, 7).Value = item.ConceptName ?? string.Empty;
                sheet.Cell(row, 8).Value = item.CanonicalRxcui ?? string.Empty;
                sheet.Cell(row, 9).Value = item.CandidatesJson;
                sheet.Cell(row, 10).Value = item.ReleaseStartDate ?? string.Empty;
                sheet.Cell(row, 11).Value = item.ReleaseEndDate ?? string.Empty;
                sheet.Cell(row, 12).Value = item.Detail ?? string.Empty;
                sheet.Cell(row, 13).Value = item.CheckedUtc;
            }
            sheet.Columns().AdjustToContents();
            await using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            return Results.File(stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"medidrift-audit-{auditId:N}.xlsx");
        }

        if (format is not ("" or "csv"))
            return Results.BadRequest(new ApiError("Supported export formats are csv and xlsx."));

        using var writer = new StringWriter();
        using (var csv = new CsvHelper.CsvWriter(writer, System.Globalization.CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(findings.Select(x => new
            {
                x.SourceRowNumber,
                ExternalId = CsvSafe(x.ExternalId),
                MedicationName = CsvSafe(x.MedicationName),
                x.IdentifierType,
                SubmittedIdentifier = CsvSafe(x.SubmittedIdentifier),
                x.Status,
                ConceptName = CsvSafe(x.ConceptName),
                x.CanonicalRxcui,
                x.CandidatesJson,
                x.ReleaseStartDate,
                x.ReleaseEndDate,
                Detail = CsvSafe(x.Detail),
                x.CheckedUtc
            }));
        }
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(writer.ToString())).ToArray(),
            "text/csv; charset=utf-8", $"medidrift-audit-{auditId:N}.csv");
    }

    private static MedicationAuditRunDto ToDto(MedicationAuditRun run)
    {
        var progress = run.TotalIdentifiers <= 0 ? (run.Status == MedicationAuditStatuses.Completed ? 100 : 0)
            : (int)Math.Clamp(Math.Round(run.ProcessedIdentifiers * 100d / run.TotalIdentifiers), 0, 100);
        return new MedicationAuditRunDto(
            run.Id, run.ImportJobId, run.Status, run.TotalRecords, run.TotalIdentifiers, run.ProcessedIdentifiers,
            progress, run.ActiveCount, run.ObsoleteCount, run.RemappedCount, run.QuantifiedCount,
            run.NotCurrentCount, run.UnknownCount, run.AlienCount, run.InvalidFormatCount,
            run.MissingIdentifierCount, run.LookupErrorCount, run.RxNormVersion, run.CreatedUtc,
            run.StartedUtc, run.CompletedUtc, run.ErrorMessage);
    }

    private static MedicationAuditFindingDto ToDto(MedicationAuditFinding finding)
    {
        var candidates = JsonSerializer.Deserialize<List<AuditCandidateDto>>(finding.CandidatesJson) ?? [];
        return new MedicationAuditFindingDto(
            finding.Id, finding.MedicationRecordId, finding.SourceRowNumber, finding.ExternalId,
            finding.MedicationName, finding.IdentifierType, finding.SubmittedIdentifier, finding.Status,
            finding.ConceptName, finding.CanonicalRxcui, candidates, finding.ReleaseStartDate,
            finding.ReleaseEndDate, finding.Detail, finding.CheckedUtc);
    }

    private static string? CsvSafe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0) return value;
        var first = trimmed[0];
        return first == '=' || first == '+' || first == '-' || first == '@' || first == '\t' || first == '\r'
            ? $"'{value}"
            : value;
    }
}
