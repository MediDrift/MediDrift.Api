using System.Text;
using ClosedXML.Excel;
using CsvHelper;
using MediDrift.Api.Data;
using MediDrift.Api.Models;
using MediDrift.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace MediDrift.Api.Endpoints;

public static class ImportEndpoints
{
    private const long MaxUploadBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/imports").WithTags("Imports");

        group.MapPost("", CreateImportAsync)
            .DisableAntiforgery()
            .WithSummary("Upload a CSV/XLSX file and create a staged import");

        group.MapGet("", ListImportsAsync).WithSummary("List recent imports");
        group.MapGet("/{id:guid}", GetImportAsync).WithSummary("Get import details and column suggestions");
        group.MapPut("/{id:guid}/mapping", SaveMappingAsync).WithSummary("Save confirmed source-to-canonical mappings");
        group.MapPost("/{id:guid}/confirm", ConfirmImportAsync).WithSummary("Normalize and persist imported medication records");
        group.MapGet("/{id:guid}/records", GetRecordsAsync).WithSummary("Get normalized records with pagination");
        group.MapGet("/{id:guid}/export", ExportRecordsAsync).WithSummary("Export normalized records as CSV or XLSX");

        return endpoints;
    }

    private static async Task<IResult> CreateImportAsync(
        HttpRequest request,
        AppDbContext db,
        ImportFileParser parser,
        ColumnMappingService mappingService,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
            return Results.BadRequest(new ApiError("Send the file as multipart/form-data using the 'file' field."));

        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
            return Results.BadRequest(new ApiError("Choose a non-empty CSV or XLSX file."));
        if (file.Length > MaxUploadBytes)
            return Results.BadRequest(new ApiError("The maximum upload size for this MVP is 10 MB."));

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".csv" or ".xlsx"))
            return Results.BadRequest(new ApiError("Unsupported file type. Upload a .csv or .xlsx file."));

        var worksheetName = form["worksheet"].ToString();
        var profileIdValue = form["mappingProfileId"].ToString();
        Dictionary<string, string>? profileMappings = null;
        string? appliedProfileName = null;

        if (Guid.TryParse(profileIdValue, out var profileId))
        {
            var profile = await db.MappingProfiles.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == profileId && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
            if (profile is not null)
            {
                profileMappings = ColumnMappingService.DeserializeMappings(profile.MappingsJson);
                appliedProfileName = profile.Name;
            }
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var parsed = await parser.ParseAsync(stream, file.FileName, worksheetName, cancellationToken);
            var suggestions = mappingService.Suggest(parsed.Headers, profileMappings);
            var job = new ImportJob
            {
                Id = Guid.NewGuid(),
                WorkspaceId = DefaultWorkspace.Id,
                OriginalFileName = Path.GetFileName(file.FileName.Replace('\\', '/')),
                FileExtension = extension,
                WorksheetName = parsed.WorksheetName,
                Status = ImportStatuses.Draft,
                TotalRows = parsed.Rows.Count,
                MappingsJson = "{}",
                AppliedProfileName = appliedProfileName,
                CreatedUtc = DateTime.UtcNow
            };

            job.Columns = suggestions.Select((suggestion, index) => new ImportColumn
            {
                SourceName = suggestion.SourceName,
                Position = index,
                SuggestedTargetField = suggestion.TargetField,
                SuggestionConfidence = suggestion.Confidence,
                NeedsReview = suggestion.NeedsReview
            }).ToList();

            job.StagedRows = parsed.Rows.Select(row => new StagedImportRow
            {
                RowNumber = row.RowNumber,
                ValuesJson = ColumnMappingService.SerializeRow(row.Values)
            }).ToList();

            db.ImportJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/imports/{job.Id}", await BuildDetailsAsync(db, job, cancellationToken));
        }
        catch (ImportFileException exception)
        {
            return Results.BadRequest(new ApiError(exception.Message));
        }
    }

    private static async Task<IResult> ListImportsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var imports = await db.ImportJobs.AsNoTracking()
            .Where(x => x.WorkspaceId == DefaultWorkspace.Id)
            .OrderByDescending(x => x.CreatedUtc)
            .Take(50)
            .Select(x => new ImportListItemDto(x.Id, x.OriginalFileName, x.Status, x.TotalRows, x.CreatedUtc, x.ImportedUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(imports);
    }

    private static async Task<IResult> GetImportAsync(Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        return job is null
            ? Results.NotFound(new ApiError("Import was not found."))
            : Results.Ok(await BuildDetailsAsync(db, job, cancellationToken));
    }

    private static async Task<IResult> SaveMappingAsync(
        Guid id,
        SaveMappingRequest request,
        AppDbContext db,
        ColumnMappingService mappingService,
        CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs
            .Include(x => x.Columns)
            .FirstOrDefaultAsync(x => x.Id == id && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (job is null) return Results.NotFound(new ApiError("Import was not found."));
        if (job.Status == ImportStatuses.Imported)
            return Results.Conflict(new ApiError("This import has already been confirmed and cannot be remapped."));

        var headers = job.Columns.OrderBy(x => x.Position).Select(x => x.SourceName).ToArray();
        var errors = mappingService.Validate(headers, request.Mappings);
        if (errors.Count > 0) return Results.ValidationProblem(errors.Select((message, index) => new { message, index }).ToDictionary(x => $"mapping_{x.index}", x => new[] { x.message }));

        var mappingDictionary = ColumnMappingService.ToDictionary(request.Mappings);
        job.MappingsJson = ColumnMappingService.SerializeMappings(mappingDictionary);
        job.Status = ImportStatuses.Mapped;

        if (!string.IsNullOrWhiteSpace(request.SaveProfileName))
        {
            var profileName = request.SaveProfileName.Trim();
            if (profileName.Length > 120)
                return Results.BadRequest(new ApiError("Mapping profile names must be 120 characters or fewer."));

            var profile = await db.MappingProfiles.FirstOrDefaultAsync(
                x => x.WorkspaceId == job.WorkspaceId && x.Name == profileName, cancellationToken);

            if (profile is null)
            {
                profile = new MappingProfile
                {
                    Id = Guid.NewGuid(),
                    WorkspaceId = job.WorkspaceId,
                    Name = profileName,
                    CreatedUtc = DateTime.UtcNow
                };
                db.MappingProfiles.Add(profile);
            }

            profile.MappingsJson = job.MappingsJson;
            profile.UpdatedUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(await BuildDetailsAsync(db, job, cancellationToken));
    }

    private static async Task<IResult> ConfirmImportAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.FirstOrDefaultAsync(
            x => x.Id == id && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (job is null) return Results.NotFound(new ApiError("Import was not found."));
        if (job.Status == ImportStatuses.Imported)
            return Results.Conflict(new ApiError("This import has already been confirmed."));

        var mappings = ColumnMappingService.DeserializeMappings(job.MappingsJson);
        if (!mappings.Values.Contains(MappingFields.Rxcui, StringComparer.Ordinal)
            && !mappings.Values.Contains(MappingFields.Ndc, StringComparer.Ordinal))
        {
            return Results.BadRequest(new ApiError("Map at least one medication identifier: RxCUI or NDC, before confirming the import."));
        }

        var stagedRows = await db.StagedImportRows.AsNoTracking()
            .Where(x => x.ImportJobId == id)
            .OrderBy(x => x.RowNumber)
            .ToListAsync(cancellationToken);

        var sourceFor = mappings
            .Where(x => x.Value != MappingFields.Ignore)
            .ToDictionary(x => x.Value, x => x.Key, StringComparer.Ordinal);

        string? ReadField(Dictionary<string, string?> values, string target)
        {
            if (!sourceFor.TryGetValue(target, out var source)) return null;
            return values.TryGetValue(source, out var value) ? NullIfBlank(value) : null;
        }

        var records = stagedRows.Select(row =>
        {
            var original = ColumnMappingService.DeserializeRow(row.ValuesJson);
            return new MedicationRecord
            {
                ImportJobId = job.Id,
                SourceRowNumber = row.RowNumber,
                ExternalId = ReadField(original, MappingFields.Id),
                MedicationName = ReadField(original, MappingFields.Name),
                RxCUI = ReadField(original, MappingFields.Rxcui),
                NDC = ReadField(original, MappingFields.Ndc),
                Strength = ReadField(original, MappingFields.Strength),
                DosageForm = ReadField(original, MappingFields.DosageForm),
                BrandName = ReadField(original, MappingFields.BrandName),
                OriginalValuesJson = row.ValuesJson
            };
        }).ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.MedicationRecords.AddRange(records);
        job.Status = ImportStatuses.Imported;
        job.ImportedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Results.Ok(new ConfirmImportResponse(
            job.Id,
            job.Status,
            records.Count,
            "The file was normalized and saved. No medication identifiers were changed or clinically validated."));
    }

    private static async Task<IResult> GetRecordsAsync(
        Guid id,
        int page,
        int pageSize,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var exists = await db.ImportJobs.AsNoTracking().AnyAsync(
            x => x.Id == id && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (!exists) return Results.NotFound(new ApiError("Import was not found."));

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize == 0 ? 50 : pageSize, 1, 200);
        var query = db.MedicationRecords.AsNoTracking().Where(x => x.ImportJobId == id);
        var total = await query.CountAsync(cancellationToken);
        var entities = await query.OrderBy(x => x.SourceRowNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var items = entities.Select(ToDto).ToArray();

        return Results.Ok(new PagedResult<MedicationRecordDto>(items, page, pageSize, total));
    }

    private static async Task<IResult> ExportRecordsAsync(
        Guid id,
        HttpRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.AsNoTracking()
            .Include(x => x.Columns)
            .FirstOrDefaultAsync(
                x => x.Id == id && x.WorkspaceId == DefaultWorkspace.Id,
                cancellationToken);

        if (job is null)
            return Results.NotFound(new ApiError("Import was not found."));

        var entities = await db.MedicationRecords.AsNoTracking()
            .Where(x => x.ImportJobId == id)
            .OrderBy(x => x.SourceRowNumber)
            .ToListAsync(cancellationToken);

        var records = entities.Select(ToDto).ToList();
        var sourceHeaders = job.Columns
            .OrderBy(x => x.Position)
            .Select(x => x.SourceName)
            .ToArray();

        var format = request.Query["format"].ToString().ToLowerInvariant();

        // Export every original source column as a separate worksheet.
        // The normalized worksheet remains available alongside the untouched source data.
        if (format == "xlsx")
        {
            using var workbook = new XLWorkbook();

            var normalizedSheet = workbook.Worksheets.Add("Normalized Records");
            var normalizedHeaders = new[]
            {
                "SourceRowNumber", "ExternalId", "MedicationName", "RxCUI",
                "NDC", "Strength", "DosageForm", "BrandName"
            };

            for (var column = 0; column < normalizedHeaders.Length; column++)
                normalizedSheet.Cell(1, column + 1).Value = normalizedHeaders[column];

            for (var index = 0; index < records.Count; index++)
            {
                var record = records[index];
                var row = index + 2;
                normalizedSheet.Cell(row, 1).Value = record.SourceRowNumber;
                normalizedSheet.Cell(row, 2).Value = record.ExternalId ?? string.Empty;
                normalizedSheet.Cell(row, 3).Value = record.MedicationName ?? string.Empty;
                normalizedSheet.Cell(row, 4).Value = record.Rxcui ?? string.Empty;
                normalizedSheet.Cell(row, 5).Value = record.Ndc ?? string.Empty;
                normalizedSheet.Cell(row, 6).Value = record.Strength ?? string.Empty;
                normalizedSheet.Cell(row, 7).Value = record.DosageForm ?? string.Empty;
                normalizedSheet.Cell(row, 8).Value = record.BrandName ?? string.Empty;
            }

            var originalSheet = workbook.Worksheets.Add("Original Source Data");
            originalSheet.Cell(1, 1).Value = "SourceRowNumber";

            for (var column = 0; column < sourceHeaders.Length; column++)
                originalSheet.Cell(1, column + 2).Value = sourceHeaders[column];

            for (var index = 0; index < entities.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entity = entities[index];
                var originalValues = ColumnMappingService.DeserializeRow(entity.OriginalValuesJson);
                var row = index + 2;

                originalSheet.Cell(row, 1).Value = entity.SourceRowNumber;

                for (var column = 0; column < sourceHeaders.Length; column++)
                {
                    originalValues.TryGetValue(sourceHeaders[column], out var value);
                    originalSheet.Cell(row, column + 2).Value = value ?? string.Empty;
                }
            }

            normalizedSheet.SheetView.FreezeRows(1);
            originalSheet.SheetView.FreezeRows(1);
            normalizedSheet.Range(1, 1, 1, normalizedHeaders.Length).Style.Font.Bold = true;
            originalSheet.Range(1, 1, 1, sourceHeaders.Length + 1).Style.Font.Bold = true;
            normalizedSheet.Columns(1, normalizedHeaders.Length).Width = 20;
            originalSheet.Columns(1, sourceHeaders.Length + 1).Width = 22;

            await using var stream = new MemoryStream();
            workbook.SaveAs(stream);

            return Results.File(
                stream.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"medidrift-import-{id:N}-complete.xlsx");
        }

        // Original-source CSV contains every imported source column, including unmapped fields.
        if (format == "source-csv")
        {
            using var writer = new StringWriter();
            using (var csv = new CsvWriter(writer, System.Globalization.CultureInfo.InvariantCulture))
            {
                csv.WriteField("SourceRowNumber");
                foreach (var header in sourceHeaders)
                    csv.WriteField(header);
                csv.NextRecord();

                foreach (var entity in entities)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var originalValues = ColumnMappingService.DeserializeRow(entity.OriginalValuesJson);
                    csv.WriteField(entity.SourceRowNumber);

                    foreach (var header in sourceHeaders)
                    {
                        originalValues.TryGetValue(header, out var value);
                        csv.WriteField(CsvSafe(value));
                    }

                    csv.NextRecord();
                }
            }

            return Results.File(
                Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(writer.ToString())).ToArray(),
                "text/csv; charset=utf-8",
                $"medidrift-import-{id:N}-original-source.csv");
        }

        // The default CSV export contains the normalized canonical fields.
        if (format is not ("" or "csv"))
            return Results.BadRequest(new ApiError("Supported formats are csv, source-csv and xlsx."));

        var csvSafeRecords = records.Select(record => record with
        {
            ExternalId = CsvSafe(record.ExternalId),
            MedicationName = CsvSafe(record.MedicationName),
            Rxcui = CsvSafe(record.Rxcui),
            Ndc = CsvSafe(record.Ndc),
            Strength = CsvSafe(record.Strength),
            DosageForm = CsvSafe(record.DosageForm),
            BrandName = CsvSafe(record.BrandName)
        }).ToList();

        using var textWriter = new StringWriter();
        using (var csv = new CsvWriter(textWriter, System.Globalization.CultureInfo.InvariantCulture))
        {
            csv.WriteRecords(csvSafeRecords);
            csv.Flush();
        }

        return Results.File(
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(textWriter.ToString())).ToArray(),
            "text/csv; charset=utf-8",
            $"medidrift-import-{id:N}.csv");
    }

    private static async Task<ImportDetailsDto> BuildDetailsAsync(
        AppDbContext db,
        ImportJob job,
        CancellationToken cancellationToken)
    {
        var columns = await db.ImportColumns.AsNoTracking()
            .Where(x => x.ImportJobId == job.Id)
            .OrderBy(x => x.Position)
            .Select(x => new ImportColumnDto(x.SourceName, x.Position, x.SuggestedTargetField, x.SuggestionConfidence, x.NeedsReview))
            .ToListAsync(cancellationToken);

        var staged = await db.StagedImportRows.AsNoTracking()
            .Where(x => x.ImportJobId == job.Id)
            .OrderBy(x => x.RowNumber)
            .Take(5)
            .ToListAsync(cancellationToken);

        var samples = staged.Select(x => ColumnMappingService.DeserializeRow(x.ValuesJson)).ToArray();
        var mappings = ColumnMappingService.DeserializeMappings(job.MappingsJson);

        return new ImportDetailsDto(
            job.Id,
            job.OriginalFileName,
            job.FileExtension,
            job.WorksheetName,
            job.Status,
            job.TotalRows,
            job.CreatedUtc,
            job.AppliedProfileName,
            columns,
            samples,
            mappings);
    }

    private static MedicationRecordDto ToDto(MedicationRecord record) => new(
        record.Id,
        record.SourceRowNumber,
        record.ExternalId,
        record.MedicationName,
        record.RxCUI,
        record.NDC,
        record.Strength,
        record.DosageForm,
        record.BrandName);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? CsvSafe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0) return value;
        return trimmed[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? $"'{value}" : value;
    }
}
