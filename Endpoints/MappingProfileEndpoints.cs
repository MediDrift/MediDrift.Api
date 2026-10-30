using MediDrift.Api.Data;
using MediDrift.Api.Models;
using MediDrift.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace MediDrift.Api.Endpoints;

public static class MappingProfileEndpoints
{
    public static IEndpointRouteBuilder MapMappingProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/mapping-profiles").WithTags("Mapping profiles");
        group.MapGet("", ListAsync).WithSummary("List saved mapping profiles");
        group.MapPost("", CreateOrUpdateAsync).WithSummary("Create or update a mapping profile");
        group.MapDelete("/{id:guid}", DeleteAsync).WithSummary("Delete a saved mapping profile");
        return endpoints;
    }

    private static async Task<IResult> ListAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var profiles = await db.MappingProfiles.AsNoTracking()
            .Where(x => x.WorkspaceId == DefaultWorkspace.Id)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(profiles.Select(x => new MappingProfileDto(
            x.Id,
            x.Name,
            ColumnMappingService.DeserializeMappings(x.MappingsJson),
            x.UpdatedUtc)));
    }

    private static async Task<IResult> CreateOrUpdateAsync(
        CreateMappingProfileRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(new ApiError("A mapping profile name is required."));
        if (name.Length > 120)
            return Results.BadRequest(new ApiError("Mapping profile names must be 120 characters or fewer."));
        if (request.Mappings is null || request.Mappings.Count == 0)
            return Results.BadRequest(new ApiError("At least one column mapping is required."));

        var errors = new List<string>();
        var seenSources = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in request.Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.SourceColumn)) errors.Add("A source column name is required.");
            else if (!seenSources.Add(mapping.SourceColumn)) errors.Add($"Source column '{mapping.SourceColumn}' is duplicated.");

            if (!MappingFields.IsSupported(mapping.TargetField)) errors.Add($"Target field '{mapping.TargetField}' is not supported.");
            else if (mapping.TargetField != MappingFields.Ignore && !seenTargets.Add(mapping.TargetField))
                errors.Add($"Target field '{mapping.TargetField}' is mapped more than once.");
        }
        if (errors.Count > 0)
            return Results.BadRequest(new ApiError("The mapping profile is invalid.", errors));

        var dictionary = ColumnMappingService.ToDictionary(request.Mappings);
        var profile = await db.MappingProfiles.FirstOrDefaultAsync(
            x => x.WorkspaceId == DefaultWorkspace.Id && x.Name == name, cancellationToken);

        if (profile is null)
        {
            profile = new MappingProfile
            {
                Id = Guid.NewGuid(),
                WorkspaceId = DefaultWorkspace.Id,
                Name = name,
                CreatedUtc = DateTime.UtcNow
            };
            db.MappingProfiles.Add(profile);
        }

        profile.MappingsJson = ColumnMappingService.SerializeMappings(dictionary);
        profile.UpdatedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new MappingProfileDto(
            profile.Id,
            profile.Name,
            dictionary,
            profile.UpdatedUtc));
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, CancellationToken cancellationToken)
    {
        var profile = await db.MappingProfiles.FirstOrDefaultAsync(
            x => x.Id == id && x.WorkspaceId == DefaultWorkspace.Id, cancellationToken);
        if (profile is null) return Results.NotFound(new ApiError("Mapping profile was not found."));
        db.MappingProfiles.Remove(profile);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}
