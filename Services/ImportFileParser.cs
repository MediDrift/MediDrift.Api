using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ClosedXML.Excel;

namespace MediDrift.Api.Services;

public sealed record ParsedRow(int RowNumber, Dictionary<string, string?> Values);

public sealed record ParsedImport(
    IReadOnlyList<string> Headers,
    IReadOnlyList<ParsedRow> Rows,
    string? WorksheetName);

public sealed class ImportFileException(string message) : Exception(message) { }

public sealed class ImportFileParser
{
    public const int MaxRows = 25_000;
    public const int MaxColumns = 100;

    public async Task<ParsedImport> ParseAsync(
        Stream stream,
        string fileName,
        string? requestedWorksheet,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        try
        {
            return extension switch
            {
                ".csv" => await ParseCsvAsync(stream, cancellationToken),
                ".xlsx" => ParseExcel(stream, requestedWorksheet),
                _ => throw new ImportFileException("Unsupported file type. Upload a .csv or .xlsx file.")
            };
        }
        catch (ImportFileException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ImportFileException("The file could not be parsed. Check that it is a valid CSV or XLSX file and try again.");
        }
    }

    private static async Task<ParsedImport> ParseCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            DetectDelimiter = true,
            IgnoreBlankLines = true,
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null
        };

        using var csv = new CsvReader(reader, configuration);
        if (!await csv.ReadAsync())
            throw new ImportFileException("The CSV file is empty.");

        csv.ReadHeader();
        var headers = NormalizeAndValidateHeaders(csv.HeaderRecord);
        var rows = new List<ParsedRow>();
        var rowNumber = 1;

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            var hasValue = false;

            for (var i = 0; i < headers.Count; i++)
            {
                var value = csv.GetField(i);
                if (!string.IsNullOrWhiteSpace(value)) hasValue = true;
                values[headers[i]] = string.IsNullOrWhiteSpace(value) ? null : value;
            }

            if (!hasValue) continue;
            if (rows.Count >= MaxRows)
                throw new ImportFileException($"The file contains more than {MaxRows:N0} data rows. Split it into smaller files for this MVP.");

            rows.Add(new ParsedRow(rowNumber, values));
        }

        if (rows.Count == 0)
            throw new ImportFileException("No data rows were found below the header row.");

        return new ParsedImport(headers, rows, null);
    }

    private static ParsedImport ParseExcel(Stream stream, string? requestedWorksheet)
    {
        try
        {
            using var workbook = new XLWorkbook(stream);
            IXLWorksheet worksheet;

            if (!string.IsNullOrWhiteSpace(requestedWorksheet))
            {
                worksheet = workbook.Worksheets.FirstOrDefault(x =>
                    string.Equals(x.Name, requestedWorksheet.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ImportFileException($"Worksheet '{requestedWorksheet}' was not found in the workbook.");
            }
            else
            {
                worksheet = workbook.Worksheets.FirstOrDefault()
                    ?? throw new ImportFileException("The Excel workbook does not contain a worksheet.");
            }

            var headerRowNumber = worksheet.FirstRowUsed()?.RowNumber()
                ?? throw new ImportFileException("The selected worksheet is empty.");
            var lastRowNumber = worksheet.LastRowUsed()?.RowNumber() ?? headerRowNumber;
            var lastColumnNumber = worksheet.Row(headerRowNumber).LastCellUsed()?.Address.ColumnNumber ?? 0;

            if (lastColumnNumber == 0)
                throw new ImportFileException("No header columns were found in the selected worksheet.");
            if (lastColumnNumber > MaxColumns)
                throw new ImportFileException($"The worksheet contains more than {MaxColumns} columns. Remove unused columns and try again.");

            var rawHeaders = Enumerable.Range(1, lastColumnNumber)
                .Select(column => worksheet.Cell(headerRowNumber, column).GetFormattedString().Trim())
                .ToArray();
            var headers = NormalizeAndValidateHeaders(rawHeaders);
            var rows = new List<ParsedRow>();

            for (var rowNumber = headerRowNumber + 1; rowNumber <= lastRowNumber; rowNumber++)
            {
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                var hasValue = false;

                for (var column = 1; column <= headers.Count; column++)
                {
                    var value = worksheet.Cell(rowNumber, column).GetFormattedString();
                    if (!string.IsNullOrWhiteSpace(value)) hasValue = true;
                    values[headers[column - 1]] = string.IsNullOrWhiteSpace(value) ? null : value;
                }

                if (!hasValue) continue;
                if (rows.Count >= MaxRows)
                    throw new ImportFileException($"The worksheet contains more than {MaxRows:N0} data rows. Split it into smaller files for this MVP.");

                rows.Add(new ParsedRow(rowNumber, values));
            }

            if (rows.Count == 0)
                throw new ImportFileException("No data rows were found below the header row.");

            return new ParsedImport(headers, rows, worksheet.Name);
        }
        catch (ImportFileException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ImportFileException("The Excel file could not be read. Make sure it is a valid, unprotected .xlsx workbook.");
        }
    }

    private static IReadOnlyList<string> NormalizeAndValidateHeaders(IReadOnlyList<string>? rawHeaders)
    {
        if (rawHeaders is null || rawHeaders.Count == 0)
            throw new ImportFileException("The first row must contain column headers.");
        if (rawHeaders.Count > MaxColumns)
            throw new ImportFileException($"The file contains more than {MaxColumns} columns.");

        var headers = rawHeaders.Select(x => x?.Trim() ?? string.Empty).ToArray();
        if (headers.Any(string.IsNullOrWhiteSpace))
            throw new ImportFileException("Every column must have a header. Please name blank columns and upload again.");

        var duplicates = headers
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();

        if (duplicates.Length > 0)
            throw new ImportFileException($"Duplicate column headers were found: {string.Join(", ", duplicates)}. Rename duplicate headers before uploading.");

        return headers;
    }
}
