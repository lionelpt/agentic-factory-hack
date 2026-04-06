using Azure.AI.Projects;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RepairPlanner;
using RepairPlanner.Models;
using RepairPlanner.Services;
using System.Text.Json;

var loggerFactory = LoggerFactory.Create(builder =>
{
	builder.ClearProviders();
	builder.AddSimpleConsole(options =>
	{
		options.SingleLine = true;
		options.TimestampFormat = "HH:mm:ss ";
	});
	builder.SetMinimumLevel(LogLevel.Information);
});

var bootstrapLogger = loggerFactory.CreateLogger("Bootstrap");

try
{
	var requiredEnv = GetRequiredEnvironmentVariables(
	[
		"AZURE_AI_PROJECT_ENDPOINT",
		"MODEL_DEPLOYMENT_NAME",
		"COSMOS_ENDPOINT",
		"COSMOS_KEY",
		"COSMOS_DATABASE_NAME"
	]);

	var endpoint = requiredEnv["AZURE_AI_PROJECT_ENDPOINT"];
	var modelDeploymentName = requiredEnv["MODEL_DEPLOYMENT_NAME"];
	var cosmosOptions = new CosmosDbOptions
	{
		Endpoint = requiredEnv["COSMOS_ENDPOINT"],
		Key = requiredEnv["COSMOS_KEY"],
		DatabaseName = requiredEnv["COSMOS_DATABASE_NAME"]
	};

	var services = new ServiceCollection();

	services.AddSingleton<ILoggerFactory>(loggerFactory);
	services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
	services.AddSingleton(cosmosOptions);
	services.AddSingleton<IFaultMappingService, FaultMappingService>();
	services.AddSingleton<CosmosDbService>();
	services.AddSingleton(_ => new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential()));
	services.AddSingleton(sp =>
	{
		var projectClient = sp.GetRequiredService<AIProjectClient>();
		var cosmosDb = sp.GetRequiredService<CosmosDbService>();
		var faultMapping = sp.GetRequiredService<IFaultMappingService>();
		var logger = sp.GetRequiredService<ILogger<RepairPlannerAgent>>();

		return new RepairPlannerAgent(projectClient, cosmosDb, faultMapping, modelDeploymentName, logger);
	});

	// await using is like Python's "async with": dispose async resources automatically at the end.
	await using var provider = services.BuildServiceProvider();

	var appLogger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
	var planner = provider.GetRequiredService<RepairPlannerAgent>();
	var isDryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));

	var inputFault = await LoadFaultFromArgumentsAsync(args, appLogger)
		?? new DiagnosedFault
		{
			Id = Guid.NewGuid().ToString("N"),
			MachineId = "TBM-1001",
			FaultType = "building_drum_vibration",
			Severity = "high",
			Description = "Persistent vibration above threshold detected during drum rotation."
		};

	var createdWorkOrder = await planner.PlanAndCreateWorkOrderAsync(inputFault, persistWorkOrder: !isDryRun);

	if (isDryRun)
	{
		appLogger.LogInformation("Dry-run mode active. No data was written to Cosmos DB.");
		var json = JsonSerializer.Serialize(createdWorkOrder, new JsonSerializerOptions
		{
			WriteIndented = true
		});
		Console.WriteLine("Generated WorkOrder (dry-run):");
		Console.WriteLine(json);
	}

	appLogger.LogInformation(
		"Created work order {WorkOrderNumber} with id {WorkOrderId}",
		createdWorkOrder.WorkOrderNumber,
		createdWorkOrder.Id);

	return;
}
catch (Exception ex)
{
	bootstrapLogger.LogError(ex, "RepairPlanner execution failed.");
	Environment.ExitCode = 1;
}

static Dictionary<string, string> GetRequiredEnvironmentVariables(IReadOnlyList<string> names)
{
	var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	var missing = new List<string>();

	foreach (var name in names)
	{
		var value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value))
		{
			missing.Add(name);
			continue;
		}

		resolved[name] = value;
	}

	if (missing.Count == 0)
	{
		return resolved;
	}

	throw new InvalidOperationException(
		$"Missing required environment variables: {string.Join(", ", missing)}");
}

static async Task<DiagnosedFault?> LoadFaultFromArgumentsAsync(string[] args, ILogger logger)
{
	var faultFileArg = args.FirstOrDefault(a => a.StartsWith("--fault-file=", StringComparison.OrdinalIgnoreCase));
	if (faultFileArg is null)
	{
		return null;
	}

	var filePath = faultFileArg["--fault-file=".Length..].Trim();
	if (string.IsNullOrWhiteSpace(filePath))
	{
		throw new InvalidOperationException("--fault-file was provided but path is empty.");
	}

	if (!File.Exists(filePath))
	{
		throw new FileNotFoundException("Fault input file not found.", filePath);
	}

	var json = await File.ReadAllTextAsync(filePath);
	var fault = JsonSerializer.Deserialize<DiagnosedFault>(json, new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true
	});

	if (fault is null)
	{
		throw new InvalidOperationException("Could not deserialize DiagnosedFault from provided file.");
	}

	// ??= means assign only if null.
	fault.Id ??= Guid.NewGuid().ToString("N");
	logger.LogInformation("Loaded diagnosed fault from {FaultFilePath}", filePath);

	return fault;
}
