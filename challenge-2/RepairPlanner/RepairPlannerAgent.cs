using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.Projects;
using Azure.AI.Projects.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using RepairPlanner.Models;
using RepairPlanner.Services;

namespace RepairPlanner;

// Primary constructor: parameters are available throughout the class body (like Python __init__).
public sealed class RepairPlannerAgent(
    AIProjectClient projectClient,
    CosmosDbService cosmosDb,
    IFaultMappingService faultMapping,
    string modelDeploymentName,
    ILogger<RepairPlannerAgent> logger)
{
    private const string AgentName = "RepairPlannerAgent";

    private const string AgentInstructions = """
        You are a Repair Planner Agent for tire manufacturing equipment.
        Generate a repair plan with tasks, timeline, and resource allocation.
        Return only valid JSON matching the WorkOrder schema.

        Output JSON with these fields:
        - workOrderNumber, machineId, title, description
        - type: corrective | preventive | emergency
        - priority: critical | high | medium | low
        - status, assignedTo, notes
        - estimatedDuration: integer minutes
        - partsUsed: [{ partId, partNumber, quantity }]
        - tasks: [{ sequence, title, description, estimatedDurationMinutes, requiredSkills, safetyNotes }]
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task EnsureAgentVersionAsync(CancellationToken cancellationToken = default)
    {
        var definition = new PromptAgentDefinition(model: modelDeploymentName)
        {
            Instructions = AgentInstructions
        };

        await projectClient.Agents.CreateAgentVersionAsync(
            AgentName,
            new AgentVersionCreationOptions(definition),
            cancellationToken);
    }

    public async Task<WorkOrder> PlanAndCreateWorkOrderAsync(
        DiagnosedFault fault,
        bool persistWorkOrder = true,
        CancellationToken cancellationToken = default)
    {
        var requiredSkills = faultMapping.GetRequiredSkills(fault.FaultType);
        var requiredParts = faultMapping.GetRequiredParts(fault.FaultType);

        var availableTechnicians = await cosmosDb.GetAvailableTechniciansWithSkillsAsync(requiredSkills, cancellationToken);
        var parts = await cosmosDb.GetPartsInventoryAsync(requiredParts, cancellationToken);

        var selectedTechnician = availableTechnicians.FirstOrDefault();

        var prompt = BuildPrompt(fault, requiredSkills, requiredParts, availableTechnicians, parts, selectedTechnician);

        await EnsureAgentVersionAsync(cancellationToken);

        var aiAgent = projectClient.GetAIAgent(name: AgentName);
        var response = await aiAgent.RunAsync(prompt, thread: null, options: null);
        var workOrder = ParseWorkOrder(response.Text, fault, selectedTechnician, parts, requiredSkills);

        // ??= means "assign only if null" (similar to Python's: x = x or default).
        workOrder.AssignedTo ??= selectedTechnician?.Id;

        if (persistWorkOrder)
        {
            var createdId = await cosmosDb.CreateWorkOrderAsync(workOrder, cancellationToken);
            workOrder.Id = createdId;
        }
        else
        {
            workOrder.Id = string.IsNullOrWhiteSpace(workOrder.Id) ? $"dryrun-{Guid.NewGuid():N}" : workOrder.Id;
            logger.LogInformation("Dry-run enabled. Work order was generated but not persisted to Cosmos DB.");
        }

        return workOrder;
    }

    private static string BuildPrompt(
        DiagnosedFault fault,
        IReadOnlyList<string> requiredSkills,
        IReadOnlyList<string> requiredParts,
        IReadOnlyList<Technician> technicians,
        IReadOnlyList<Part> parts,
        Technician? selectedTechnician)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Create a work order JSON for this diagnosed fault.");
        sb.AppendLine($"Fault: {JsonSerializer.Serialize(fault, JsonOptions)}");
        sb.AppendLine($"RequiredSkills: {JsonSerializer.Serialize(requiredSkills, JsonOptions)}");
        sb.AppendLine($"RequiredParts: {JsonSerializer.Serialize(requiredParts, JsonOptions)}");
        sb.AppendLine($"AvailableTechnicians: {JsonSerializer.Serialize(technicians, JsonOptions)}");
        sb.AppendLine($"AvailableParts: {JsonSerializer.Serialize(parts, JsonOptions)}");
        sb.AppendLine($"PreferredAssignedTechnicianId: {selectedTechnician?.Id ?? "null"}");
        sb.AppendLine("Return only JSON. Do not include markdown.");

        return sb.ToString();
    }

    private WorkOrder ParseWorkOrder(
        string? rawText,
        DiagnosedFault fault,
        Technician? selectedTechnician,
        IReadOnlyList<Part> parts,
        IReadOnlyList<string> requiredSkills)
    {
        WorkOrder? parsed = null;

        if (!string.IsNullOrWhiteSpace(rawText))
        {
            var json = ExtractJsonObject(rawText);
            parsed = JsonSerializer.Deserialize<WorkOrder>(json, JsonOptions);
        }

        parsed ??= BuildFallbackWorkOrder(fault, selectedTechnician, parts, requiredSkills);

        // ?? means "use right side when null" (similar to Python's `or`).
        parsed.WorkOrderNumber = string.IsNullOrWhiteSpace(parsed.WorkOrderNumber)
            ? $"WO-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : parsed.WorkOrderNumber;
        parsed.MachineId = string.IsNullOrWhiteSpace(parsed.MachineId) ? fault.MachineId : parsed.MachineId;
        parsed.SourceFaultType = string.IsNullOrWhiteSpace(parsed.SourceFaultType) ? fault.FaultType : parsed.SourceFaultType;
        parsed.Type = string.IsNullOrWhiteSpace(parsed.Type) ? "corrective" : parsed.Type;
        parsed.Priority = string.IsNullOrWhiteSpace(parsed.Priority) ? fault.Severity : parsed.Priority;
        parsed.Status = string.IsNullOrWhiteSpace(parsed.Status) ? "open" : parsed.Status;
        parsed.AssignedTo ??= selectedTechnician?.Id;

        if (parsed.PartsUsed.Count == 0)
        {
            parsed.PartsUsed = parts.Select(p => new WorkOrderPartUsage
            {
                PartId = p.Id,
                PartNumber = p.PartNumber,
                Quantity = 1
            }).ToList();
        }

        if (parsed.Tasks.Count == 0)
        {
            parsed.Tasks =
            [
                new RepairTask
                {
                    Sequence = 1,
                    Title = $"Diagnose and repair {fault.FaultType}",
                    Description = fault.Description,
                    EstimatedDurationMinutes = 60,
                    RequiredSkills = requiredSkills.ToList(),
                    SafetyNotes = "Use lockout-tagout procedure before intervention."
                }
            ];
        }

        if (parsed.EstimatedDuration <= 0)
        {
            parsed.EstimatedDuration = parsed.Tasks.Sum(t => t.EstimatedDurationMinutes);
        }

        return parsed;
    }

    private static WorkOrder BuildFallbackWorkOrder(
        DiagnosedFault fault,
        Technician? selectedTechnician,
        IReadOnlyList<Part> parts,
        IReadOnlyList<string> requiredSkills)
    {
        return new WorkOrder
        {
            MachineId = fault.MachineId,
            Title = $"Repair plan for {fault.FaultType}",
            Description = fault.Description,
            Type = "corrective",
            Priority = fault.Severity,
            Status = "open",
            AssignedTo = selectedTechnician?.Id,
            Notes = "Generated fallback work order because agent output was empty or invalid.",
            EstimatedDuration = 60,
            SourceFaultType = fault.FaultType,
            PartsUsed = parts.Select(p => new WorkOrderPartUsage
            {
                PartId = p.Id,
                PartNumber = p.PartNumber,
                Quantity = 1
            }).ToList(),
            Tasks =
            [
                new RepairTask
                {
                    Sequence = 1,
                    Title = "Initial inspection",
                    Description = $"Inspect machine {fault.MachineId} for fault {fault.FaultType}",
                    EstimatedDurationMinutes = 30,
                    RequiredSkills = requiredSkills.ToList(),
                    SafetyNotes = "Follow PPE and lockout-tagout requirements."
                },
                new RepairTask
                {
                    Sequence = 2,
                    Title = "Execute repair",
                    Description = "Replace/adjust failed components and validate the machine.",
                    EstimatedDurationMinutes = 30,
                    RequiredSkills = requiredSkills.ToList(),
                    SafetyNotes = "Validate all guards and interlocks before restart."
                }
            ]
        };
    }

    private string ExtractJsonObject(string text)
    {
        var normalized = text.Trim();

        if (normalized.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = normalized.IndexOf('\n');
            if (firstNewline >= 0)
            {
                normalized = normalized[(firstNewline + 1)..];
            }

            var lastFence = normalized.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0)
            {
                normalized = normalized[..lastFence].Trim();
            }
        }

        var firstBrace = normalized.IndexOf('{');
        var lastBrace = normalized.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            logger.LogWarning("Agent response did not contain a valid JSON object. Falling back.");
            return "{}";
        }

        return normalized[firstBrace..(lastBrace + 1)];
    }
}