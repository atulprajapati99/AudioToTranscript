using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AudioToTranscript.Functions;

/// <summary>
/// Runs daily at 02:00 UTC. Deletes audit rows where RetainUntil &lt; today.
/// </summary>
public class CleanupFunction
{
    private readonly TableClient _table;
    private readonly ILogger<CleanupFunction> _logger;

    public CleanupFunction(IConfiguration config, ILogger<CleanupFunction> logger)
    {
        _logger = logger;
        var connStr   = config.GetValue<string>("AzureWebJobsStorage")!;
        var tableName = config.GetValue<string>("TABLE_AUDIT_LOG") ?? "AudioProcessingLog";
        _table = new TableClient(connStr, tableName);
    }

    [Function("CleanupAuditLog")]
    public async Task Run([TimerTrigger("0 0 2 * * *")] TimerInfo timer)
    {
        var today    = DateTime.UtcNow.ToString("yyyy-MM-dd");
        int deleted  = 0;
        int errors   = 0;

        _logger.LogInformation("CleanupAuditLog starting. Today={Today}", today);

        await foreach (var entity in _table.QueryAsync<TableEntity>(
            filter: $"RetainUntil lt '{today}'"))
        {
            try
            {
                await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey, entity.ETag);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete entity PK={PK} RK={RK}", entity.PartitionKey, entity.RowKey);
                errors++;
            }
        }

        _logger.LogInformation("CleanupAuditLog complete. Deleted={Deleted} Errors={Errors}", deleted, errors);
    }
}
