using MediDrift.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace MediDrift.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<ImportColumn> ImportColumns => Set<ImportColumn>();
    public DbSet<StagedImportRow> StagedImportRows => Set<StagedImportRow>();
    public DbSet<MappingProfile> MappingProfiles => Set<MappingProfile>();
    public DbSet<MedicationRecord> MedicationRecords => Set<MedicationRecord>();
    public DbSet<MedicationAuditRun> MedicationAuditRuns => Set<MedicationAuditRun>();
    public DbSet<MedicationAuditFinding> MedicationAuditFindings => Set<MedicationAuditFinding>();
    public DbSet<TerminologyCacheEntry> TerminologyCacheEntries => Set<TerminologyCacheEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });

        modelBuilder.Entity<ImportJob>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.FileExtension).HasMaxLength(12).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.MappingsJson).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.CreatedUtc });
            entity.HasOne(x => x.Workspace).WithMany(x => x.Imports).HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImportColumn>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceName).HasMaxLength(180).IsRequired();
            entity.HasIndex(x => new { x.ImportJobId, x.Position }).IsUnique();
            entity.HasOne(x => x.ImportJob).WithMany(x => x.Columns).HasForeignKey(x => x.ImportJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StagedImportRow>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.ImportJobId, x.RowNumber }).IsUnique();
            entity.Property(x => x.ValuesJson).IsRequired();
            entity.HasOne(x => x.ImportJob).WithMany(x => x.StagedRows).HasForeignKey(x => x.ImportJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MappingProfile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.MappingsJson).IsRequired();
            entity.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique();
            entity.HasOne(x => x.Workspace).WithMany(x => x.MappingProfiles).HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MedicationRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalId).HasMaxLength(180);
            entity.Property(x => x.MedicationName).HasMaxLength(500);
            entity.Property(x => x.RxCUI).HasMaxLength(32);
            entity.Property(x => x.NDC).HasMaxLength(64);
            entity.Property(x => x.Strength).HasMaxLength(120);
            entity.Property(x => x.DosageForm).HasMaxLength(120);
            entity.Property(x => x.BrandName).HasMaxLength(250);
            entity.HasIndex(x => new { x.ImportJobId, x.SourceRowNumber }).IsUnique();
            entity.HasOne(x => x.ImportJob).WithMany(x => x.MedicationRecords).HasForeignKey(x => x.ImportJobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MedicationAuditRun>(entity =>
        {
            entity.ToTable("MedicationAuditRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.RxNormVersion).HasMaxLength(40);
            entity.Property(x => x.ErrorMessage).HasMaxLength(1000);
            entity.HasIndex(x => new { x.WorkspaceId, x.ImportJobId, x.CreatedUtc });
            entity.HasOne<Workspace>()
                .WithMany()
                .HasForeignKey(x => x.WorkspaceId)
                .OnDelete(DeleteBehavior.NoAction);

            entity.HasOne<ImportJob>()
                .WithMany()
                .HasForeignKey(x => x.ImportJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MedicationAuditFinding>(entity =>
        {
            entity.ToTable("MedicationAuditFindings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdentifierType).HasMaxLength(12).IsRequired();
            entity.Property(x => x.SubmittedIdentifier).HasMaxLength(80);
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.ExternalId).HasMaxLength(180);
            entity.Property(x => x.MedicationName).HasMaxLength(500);
            entity.Property(x => x.CanonicalRxcui).HasMaxLength(32);
            entity.Property(x => x.CandidatesJson).IsRequired();
            entity.HasIndex(x => new { x.AuditRunId, x.SourceRowNumber });
            entity.HasIndex(x => new { x.AuditRunId, x.Status });
            entity.HasOne<MedicationAuditRun>()
                .WithMany()
                .HasForeignKey(x => x.AuditRunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<MedicationRecord>()
                .WithMany()
                .HasForeignKey(x => x.MedicationRecordId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<TerminologyCacheEntry>(entity =>
        {
            entity.ToTable("TerminologyCacheEntries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdentifierType).HasMaxLength(12).IsRequired();
            entity.Property(x => x.Identifier).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.CandidatesJson).IsRequired();
            entity.Property(x => x.RawResponseJson).IsRequired();
            entity.HasIndex(x => new { x.IdentifierType, x.Identifier }).IsUnique();
        });
    }
}
