
import asyncio
import os

import requests
from azure.ai.projects import AIProjectClient
from azure.ai.projects.models import MCPTool, PromptAgentDefinition
from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from dotenv import load_dotenv


def get_required_env(name: str) -> str:
    """Return a required environment variable or raise a clear error."""
    value = os.environ.get(name)
    if not value:
        raise ValueError(f"Missing required environment variable: {name}")
    return value


load_dotenv(override=True)
project_endpoint = get_required_env("AZURE_AI_PROJECT_ENDPOINT")
model_name = get_required_env("MODEL_DEPLOYMENT_NAME")

# Configuration
knowledge_base_name = "machine-kb"
search_endpoint = get_required_env("SEARCH_SERVICE_ENDPOINT").rstrip("/") + "/"
machine_wiki_mcp_endpoint = (
    f"{search_endpoint}knowledgebases/{knowledge_base_name}/mcp?api-version=2025-11-01-preview"
)
machine_data_mcp_endpoint = get_required_env("MACHINE_MCP_SERVER_ENDPOINT")
apim_subscription_key = get_required_env("APIM_SUBSCRIPTION_KEY")


project_resource_id = get_required_env("AZURE_AI_PROJECT_RESOURCE_ID")
machine_data_connection_name = "machine-data-connection"
machine_wiki_connection_name = "machine-wiki-connection"


def create_apim_mcp_connection(connection_name, mcp_endpoint):
    """Create APIM MCP connection with subscription key credentials"""
    credential = DefaultAzureCredential()
    bearer_token_provider = get_bearer_token_provider(
        credential, "https://management.azure.com/.default")
    headers = {
        "Authorization": f"Bearer {bearer_token_provider()}",
    }

    response = requests.put(
        f"https://management.azure.com{project_resource_id}/connections/{connection_name}?api-version=2025-10-01-preview",
        headers=headers,
        json={
            "name": connection_name,
            "type": "Microsoft.MachineLearningServices/workspaces/connections",
            "properties": {
                "authType": "CustomKeys",
                "category": "RemoteTool",
                "target": mcp_endpoint,
                "isSharedToAll": True,
                "credentials": {"keys": {"Ocp-Apim-Subscription-Key": apim_subscription_key}},
                "metadata": {"type": "custom_MCP"}
            }
        },
        timeout=30,
    )

    response.raise_for_status()
    print(f"✅ Connection '{connection_name}' created successfully.")

async def main():
    try:
        # Register APIM MCP servers as project connections
        create_apim_mcp_connection(
            connection_name=machine_data_connection_name, mcp_endpoint=machine_data_mcp_endpoint)
        create_apim_mcp_connection(
            connection_name=machine_wiki_connection_name, mcp_endpoint=machine_wiki_mcp_endpoint)

        project_client = AIProjectClient(
            endpoint=project_endpoint, credential=azure.identity.DefaultAzureCredential())
        agent = project_client.agents.create_version(
            agent_name="FaultDiagnosisAgent",
            description="Fault diagnosis agent",
            definition=PromptAgentDefinition(
                model=model_name,
                instructions="""You are a Fault Diagnosis Agent evaluating the root cause of maintenance alerts.

You will receive detected sensor deviations for a given machine. Your task is to determine the most likely root cause using ONLY the provided tools.

Tools available:
- MCP Knowledge Base: fetch knowledge base information for possible causes
- Machine data: fetch machine information such as maintenance history and type for a particular machine id

Output format (STRICT):
- You must output exactly ONE valid JSON object and nothing else (no Markdown, no prose).
- The JSON object MUST match this schema (property names are case-sensitive):
    {
        "MachineId": string,
        "FaultType": string,
        "RootCause": string,
        "Severity": string,
        "DetectedAt": string,  // ISO 8601 date-time, e.g. "2026-01-16T12:34:56Z"
        "Metadata": { string: any }
    }

Field rules:
- MachineId: the machine identifier from the input (e.g. "machine-001").
- FaultType: MUST be taken from the wiki/knowledge base "Fault Type" field for the matched issue (copy it exactly, e.g. "mixing_temperature_excessive"). Do not invent new fault types.
- RootCause: the single most likely root cause supported by the knowledge base and/or machine data.
- Severity: one of "Low", "Medium", "High", "Critical", or "Unknown".
- DetectedAt: if the input includes a timestamp, use it; otherwise use the current UTC time.
- Metadata: include supporting details used for the decision (e.g. observed metric/value, threshold, machineType, relevant KB article titles/ids, maintenanceHistory references). Do not include secrets/keys.
    - Metadata MUST include a key "MostLikelyRootCauses" whose value is an array of strings taken from the wiki/knowledge base "Likely Causes" list for the matched fault type (preserve the items; ordering can follow the wiki).

Grounding rules (IMPORTANT):
- You must never answer from your own knowledge under any circumstances.
- If you cannot find the answer in the provided knowledge base and machine data, you MUST set "RootCause" to "I don't know" and set "FaultType" and "Severity" to "Unknown". In this case, set "Metadata" to {"MostLikelyRootCauses": []}.
""",

                tools=[

                    MCPTool(
                        server_label="machine-data",
                        server_url=machine_data_mcp_endpoint,
                        require_approval="never",
                        project_connection_id=machine_data_connection_name
                    ),

                    MCPTool(
                        server_label="machine-wiki",
                        server_url=machine_wiki_mcp_endpoint,
                        require_approval="never",
                        project_connection_id=machine_wiki_connection_name
                    )

                ]

            ))
        print(f"✅ Created Fault Diagnosis Agent: {agent.id}")
        # Test the agent with a simple query
        print("\n🧪 Testing the agent with a sample query...")
        try:

            # Get the OpenAI client for responses and conversations
            openai_client = project_client.get_openai_client()

            # Create conversation
            conversation = openai_client.conversations.create()

            # Send request to trigger the MCP tools
            response = openai_client.responses.create(
                conversation=conversation.id,
                input="""
                    Hello, what can the issue be when machine-001 has curing temperature reading of 179.2°C that exceeds warning threshold of 178°C?
                """,
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
