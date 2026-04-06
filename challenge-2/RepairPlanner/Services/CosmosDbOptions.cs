namespace RepairPlanner.Services;

public sealed class CosmosDbOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string Key { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;

    public string TechniciansContainerName { get; init; } = "Technicians";
    public string PartsContainerName { get; init; } = "PartsInventory";
    public string WorkOrdersContainerName { get; init; } = "WorkOrders";
}