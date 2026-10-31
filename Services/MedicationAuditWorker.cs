using MediDrift.Api.Data;
using MediDrift.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace MediDrift.Api.Services;

public sealed class MedicationAuditWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MedicationAuditWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recover interrupted work without losing the user's last control request.
        await using (var startupScope = scopeFactory.CreateAsyncScope())
        {
            var startupDb = startupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var now = DateTime.UtcNow;

            await startupDb.MedicationAuditRuns
                .Where(x => x.Status == MedicationAuditStatuses.Pausing)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, MedicationAuditStatuses.Paused), stoppingToken);

            await startupDb.MedicationAuditRuns
                .Where(x => x.Status == MedicationAuditStatuses.Cancelling)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.Status, MedicationAuditStatuses.Cancelled)
                    .SetProperty(x => x.CompletedUtc, now)
                    .SetProperty(x => x.ErrorMessage, "Cancelled during service restart. Completed findings have been retained."), stoppingToken);

            await startupDb.MedicationAuditRuns
                .Where(x => x.Status == MedicationAuditStatuses.Running)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, MedicationAuditStatuses.Queued), stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Guid? nextRunId;
                await using (var scope = scopeFactory.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    nextRunId = await db.MedicationAuditRuns.AsNoTracking()
                        .Where(x => x.Status == MedicationAuditStatuses.Queued)
                        .OrderBy(x => x.CreatedUtc)
                        .Select(x => (Guid?)x.Id)
                        .FirstOrDefaultAsync(stoppingToken);
                }

                if (nextRunId is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                await using var runScope = scopeFactory.CreateAsyncScope();
                var service = runScope.ServiceProvider.GetRequiredService<MedicationAuditService>();
                await service.ProcessRunAsync(nextRunId.Value, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unexpected medication audit worker error.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}
