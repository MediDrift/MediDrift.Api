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
        // A process restart should not strand an audit in Running state.
        await using (var startupScope = scopeFactory.CreateAsyncScope())
        {
            var startupDb = startupScope.ServiceProvider.GetRequiredService<AppDbContext>();
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
