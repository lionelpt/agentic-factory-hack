using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;

namespace RepairPlanner.Services;

public sealed class CosmosDbService : IAsyncDisposable
{
    private readonly CosmosClient _client;
    private readonly Container _techniciansContainer;
    private readonly Container _partsContainer;
    private readonly Container _workOrdersContainer;
    private readonly ILogger<CosmosDbService> _logger;

    public CosmosDbService(CosmosDbOptions options, ILogger<CosmosDbService> logger)
    {
        _logger = logger;

        _client = new CosmosClient(options.Endpoint, options.Key, new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway
        });

        var database = _client.GetDatabase(options.DatabaseName);
        _techniciansContainer = database.GetContainer(options.TechniciansContainerName);
        _partsContainer = database.GetContainer(options.PartsContainerName);
        _workOrdersContainer = database.GetContainer(options.WorkOrdersContainerName);
    }

    public async Task<IReadOnlyList<Technician>> GetAvailableTechniciansWithSkillsAsync(
        IReadOnlyList<string> requiredSkills,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE c.isAvailable = true");
            var iterator = _techniciansContainer.GetItemQueryIterator<Technician>(query);
            var technicians = new List<Technician>();

            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                technicians.AddRange(page);
            }

            var required = new HashSet<string>(requiredSkills, StringComparer.OrdinalIgnoreCase);
            var matches = technicians
                .Where(t => required.All(skill => t.Skills.Contains(skill, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            return matches;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch technicians for required skills");
            throw;
        }
    }

    public async Task<IReadOnlyList<Part>> GetPartsInventoryAsync(
        IReadOnlyList<string> partNumbers,
        CancellationToken cancellationToken = default)
    {
        if (partNumbers.Count == 0)
        {
            return [];
        }

        try
        {
            var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(@partNumbers, c.partNumber)")
                .WithParameter("@partNumbers", partNumbers);

            var iterator = _partsContainer.GetItemQueryIterator<Part>(query);
            var parts = new List<Part>();

            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(cancellationToken);
                parts.AddRange(page);
            }

            return parts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch parts inventory");
            throw;
        }
    }

    public async Task<string> CreateWorkOrderAsync(WorkOrder workOrder, CancellationToken cancellationToken = default)
    {
        workOrder.Id = string.IsNullOrWhiteSpace(workOrder.Id) ? Guid.NewGuid().ToString("N") : workOrder.Id;
        workOrder.CreatedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            var response = await _workOrdersContainer.UpsertItemAsync(
                workOrder,
                new PartitionKey(workOrder.Status),
                cancellationToken: cancellationToken);

            _logger.LogInformation("Work order created with id {WorkOrderId}", response.Resource.Id);
            return response.Resource.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create work order");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await ValueTask.CompletedTask;
    }
}