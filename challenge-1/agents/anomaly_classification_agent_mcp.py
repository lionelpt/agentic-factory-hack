import asyncio
import os

import requests
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import MCPTool, PromptAgentDefinition
from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from dotenv import load_dotenv


def get_required_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise ValueError(f"Missing required environment variable: {name}")
    return value


# Configuration
load_dotenv(override=True)
project_endpoint = get_required_env("AZURE_AI_PROJECT_ENDPOINT")
project_resource_id = get_required_env("AZURE_AI_PROJECT_RESOURCE_ID")
model_name = get_required_env("MODEL_DEPLOYMENT_NAME")


# MCP configuration
mcp_endpoint = get_required_env("MACHINE_MCP_SERVER_ENDPOINT")
apim_subscription_key = get_required_env("APIM_SUBSCRIPTION_KEY")
machine_data_connection_name = "machine-data-connection"
maintenance_data_connection_name = "maintenance-data-connection"
machine_data_mcp_endpoint = get_required_env("MACHINE_MCP_SERVER_ENDPOINT")
maintenance_data_mcp_endpoint = get_required_env(
    "MAINTENANCE_MCP_SERVER_ENDPOINT")

# DRY_RUN mode: set DRY_RUN=1 to skip network/API calls and agent creation
DRY_RUN = os.environ.get("DRY_RUN", "0") == "1"


class DummyAgent:
    def __init__(self, name="dry-run-agent"):
        self.id = "dry-run-agent-id"
        self.name = name


def create_apim_mcp_connection(connection_name, mcp_endpoint):
    # Provide connection details
    credential = DefaultAzureCredential()
    project_connection_name = connection_name

    # Get bearer token for authentication
    bearer_token_provider = get_bearer_token_provider(
        credential, "https://management.azure.com/.default")
    headers = {
        "Authorization": f"Bearer {bearer_token_provider()}",
    }

    # Create project connection
    response = requests.put(
        f"https://management.azure.com{project_resource_id}/connections/{project_connection_name}?api-version=2025-10-01-preview",
        headers=headers,
        json={
            "name": project_connection_name,
            "type": "Microsoft.MachineLearningServices/workspaces/connections",
            "properties": {
                "authType": "CustomKeys",
                "category": "RemoteTool",
                "target": mcp_endpoint,
                "isSharedToAll": True,
                "credentials":  {"keys": {"Ocp-Apim-Subscription-Key": apim_subscription_key}},
                "metadata": {"type": "custom_MCP"}
            }
        },
        timeout=30,
    )

    response.raise_for_status()
    print(
        f"✅ Connection '{project_connection_name}' created successfully.")


async def main():
    try:
        # Register APIM MCP servers as project connection
        if not DRY_RUN:
            create_apim_mcp_connection(
                connection_name="machine-data-connection", mcp_endpoint=machine_data_mcp_endpoint)
            create_apim_mcp_connection(
                connection_name="maintenance-data-connection", mcp_endpoint=maintenance_data_mcp_endpoint)
        else:
            print("DRY_RUN=1: skipping creation of APIM MCP connections")

        # Create Agent
        project_client = AIProjectClient(
            endpoint=project_endpoint, credential=DefaultAzureCredential())

        if not DRY_RUN:
            agent = project_client.agents.create_version(
                agent_name="AnomalyClassificationAgent",
                description="Anomaly classification agent",
                definition=PromptAgentDefinition(
                    model=model_name,
                instructions="""You are a Anomaly Classification Agent evaluating machine anomalies for warning and critical threshold violations.
                        You will receive anomaly data for a given machine. Your task is to:
                        - Validate each metric against the threshold values 
                        - Raise an alert for maintenance if any critical or warning violations were found

                        You have access to the following tools:
                        - machine-data: fetch machine information such as type for a particular machine id
                        - maintenance-data: fetch threshold rules for different metrics per machine type

                        Use these functions to extract and validate the anomaly data.

                        Output should be:
                        - alerts with format:
                            {
                            "machineId": "<machine_id>",
                            "status": "high" | "medium",
                            "alerts": [ {"name": "metricName1", "severity": "threshold", "description": "metric1 exceeded value x}, { "name": "metricName2", ... ],
                            "summary": {
                                "totalRecordsProcessed": <int>,
                                "violations": { "critical": <int>, "warning": <int> }
                            }
                            }
                        - summary: human readable summary of the anomalies 

                        """,

                tools=[

                    MCPTool(
                        server_label="machine-data",
                        server_url=machine_data_mcp_endpoint,
                        require_approval="never",
                        project_connection_id=machine_data_connection_name
                    ),
                    MCPTool(
                        server_label="maintenance-data",
                        server_url=maintenance_data_mcp_endpoint,
                        require_approval="never",
                        project_connection_id=maintenance_data_connection_name
                    )

                ]
            )

        )

            print(f"✅ Created Anomaly Classification Agent: {agent.id}")
        else:
            agent = DummyAgent(name="AnomalyClassificationAgent-dryrun")
            print(f"DRY_RUN=1: simulated creation of agent: {agent.name}")

        # Test the agent with a simple query
        print("\n🧪 Testing the agent with a sample query...")
        try:
            pass
            # Get the OpenAI client for responses and conversations
            openai_client = project_client.get_openai_client()

            # Create conversation
            conversation = openai_client.conversations.create()

            # Ask a question
            response = openai_client.responses.create(
                conversation=conversation.id,
                input='Hello, can you classify the following anomalies for machine-001: [{"metric": "curing_temperature", "value": 179.2},{"metric": "cycle_time", "value": 14.5}]',
                extra_body={"agent_reference": {"name": agent.name,
                                                "type": "agent_reference"}},
            )

            print(f"✅ Agent response: {response.output_text}")
        except Exception as test_error:
            print(
                f"⚠️  Agent test failed (but agent was still created): {test_error}")

        return agent

    except Exception as e:
        print(f"❌ Error creating agent: {e}")
        print("Make sure you have run 'az login' and have proper Azure credentials configured.")
        return None

if __name__ == "__main__":
    asyncio.run(main())
