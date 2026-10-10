# Spark Event Analyzer — Backend

Minimal ASP.NET Core (net8.0) backend that fetches a Spark run's data from a
**Spark History Server REST API** and returns a compact summary (the seed of the
`preprocess_events` step). This is the first slice of the
[hackathon plan](../../../docs/SparkEventAnalyzer-HackathonPlan.md).

## Component status

Components come from the [hackathon plan §10](../../../docs/SparkEventAnalyzer-HackathonPlan.md).

| # | Component | Status | Notes |
|---|-----------|--------|-------|
| 1 | Backend API (Container Apps) | 🟡 Partial | Minimal API + `/health` done. Entra Easy Auth, Managed Identity, and ACA deploy still to do. |
| 2 | Agent (run loop) | 🟢 Done | Microsoft Agent Framework agent + `get_spark_run_summary` tool. **Both** runtimes implemented & building: `AzureOpenAIAnalystAgent` (dev/demo) and `FoundryAnalystAgent` (prod), selected by `Agent:Provider`. |
| 3 | `fetch_spark_events` (REST) | 🟢 Done | `SparkHistoryClient` for History Server `/api/v1` (apps/jobs/stages/executors). One-cluster path; auth via `DefaultAzureCredential`. |
| 4 | `preprocess_events` | 🟡 Partial | `SparkRunSummarizer` reduces REST data to failures/skew/spill/GC signals. Raw event-log stream-parsing (stack frames, SQL plan) not yet. |
| 5 | Code correlation (ADO MCP → AI Search) | 🔴 Not started | Map failures/stack frames to code + recent PRs. |
| 6 | Synthesis + output | 🟡 Partial | Agent produces failure→cause→next-steps text. Structured schema + citations pending. |
| 7 | UI (thin) | 🔴 Not started | Sign-in + `appId` input calling the backend. |
| 8 | Cross-cutting (obs / secrets / caching / workflow) | 🔴 Not started | OpenTelemetry, Key Vault, caching, optional Agent Framework workflow. |

Legend: 🟢 Done · 🟡 Partial · 🔴 Not started

## Agent runtime: pluggable provider (prod-ready design)

The agent runtime is a **swappable adapter behind `IAnalystAgent`**, selected by config
`Agent:Provider`. Endpoints and tools are identical for all — only the runtime differs.

| Provider | Adapter | What it is | When |
|---|---|---|---|
| `AzureOpenAI` (default) | `AzureOpenAIAnalystAgent` | In-process agent over a bare Azure OpenAI resource | Dev, CI, offline, demo |
| `Foundry` | `FoundryAnalystAgent` | In-process agent (`AsAIAgent`) pointed at a Foundry project — Foundry as **model provider** | Prod (ephemeral, code-owned agent) |
| `PromptAgent` | `PromptAgentAnalystAgent` | Promotes the agent to a persistent, versioned Foundry **prompt agent** (`ToPromptAgentAsync`) — Foundry as **managed agent runtime** | Prod (governed: portal-visible, versioned, service-managed) |

- Switching is a **config change** (`Agent:Provider`), not a rewrite. The tools
  (`SparkTools`, `SparkHistoryClient`, summarizer) and the `/api/analyze` endpoint stay put.
- **Tools always execute in the backend** regardless of provider — they need the Managed
  Identity + network to reach the Spark History Server, which the managed runtime can't.
- `Foundry` vs `PromptAgent`: `AsAIAgent` runs an **ephemeral in-process** agent that just uses
  the Foundry project's model; `ToPromptAgentAsync` **registers a server-side prompt agent** in the
  project (governance, versioning, portal visibility). Both re-supply `SparkTools` so the function
  callback runs in the backend.
- All three build today. Running `Foundry` needs `az login` + **Foundry User** RBAC on the Foundry
  project; `PromptAgent` publishes a server-side agent and needs **Foundry Project Manager**. The
  model deployment must exist. (Note: the old `Azure AI Developer` role does NOT grant Foundry
  project/agent access — it is scoped to ML workspaces/hubs.)

## Services layout

```
Services/
  Agents/
    IAnalystAgent.cs        (port — the agent runtime abstraction)
    AgentOptions.cs         (provider selector: Agent:Provider)
    Providers/              AzureOpenAIAnalystAgent (+ AzureOpenAIOptions),
                            FoundryAnalystAgent, PromptAgentAnalystAgent
    Tools/                  SparkTools (the get_spark_run_summary function tool)
  SparkHistory/             ISparkHistoryClient, SparkHistoryClient, SparkHistoryOptions
  Summarization/            SparkRunSummarizer
```

## What's implemented
- **`SparkHistoryClient`** — REST client for the Spark History Server `/api/v1` API
  (applications, jobs, stages, executors). Supports Entra ID bearer auth via
  `DefaultAzureCredential` when a scope is configured (Synapse-style).
- **`SparkRunSummarizer`** — reduces the run to failures, hot stages, skew/spill
  suspects, and executor GC/loss signals → compact `SparkRunSummary`.
- **Minimal API** endpoints (see below).

> Isolated project: local `Directory.Build.props/targets` and `Directory.Packages.props`
> keep it out of the repo's net472/CoreXT build.

## Configure
The Spark source (**platform + cluster/workspace**) is chosen **per request**; endpoint shapes
are templated in [appsettings.json](appsettings.json) → `SparkHistory` so they can be adjusted
without code changes.

| Platform | Base URL template | Auth |
|---|---|---|
| **HDInsight** | `https://{cluster}/sparkhistory` (+ `/api/v1/...`) | Gateway **Basic** (`HDInsight:Username` / `HDInsight:Password`) |
| **Synapse** | `https://{workspace}.dev.azuresynapse.net/sparkhistory/{sparkPool}/livyid/{livyId}` *(adjust to your REST base)* | **Entra** bearer (`Synapse:Scope`, default `https://dev.azuresynapse.net/.default`) |
| **Generic** | `Generic:BaseUrl` (e.g. `http://localhost:18080`) | Optional bearer (`Generic:AuthScope`) |

Per-request fields (on `POST /api/analyze` body, or as query params on the `/api/spark/*` GETs):

| Field | Used by | Example |
|---|---|---|
| `platform` | all (inferred from `cluster`/`workspace` if omitted) | `HDInsight` \| `Synapse` |
| `appId` | all | `application_1700000000000_0001` |
| `attemptId` | all | `1` |
| `cluster` | HDInsight | `my-cluster.azurehdinsight.net` |
| `workspace`, `sparkPool`, `livyId` | Synapse | `<your-synapse-workspace>`, `<your-spark-pool>`, `50081` |

```powershell
# HDInsight
$b = @{ platform='HDInsight'; cluster='my-cluster.azurehdinsight.net'; appId='application_1700000000000_0001'; attemptId='1'; question='Why did it fail?' } | ConvertTo-Json
Invoke-RestMethod https://<app-fqdn>/api/analyze -Method Post -Body $b -ContentType application/json

# Synapse
$b = @{ platform='Synapse'; workspace='<your-synapse-workspace>'; sparkPool='<your-spark-pool>'; livyId='50081'; appId='application_1700000000000_0001'; attemptId='1' } | ConvertTo-Json
Invoke-RestMethod https://<app-fqdn>/api/analyze -Method Post -Body $b -ContentType application/json
```

Auth uses `DefaultAzureCredential` for the Entra paths (`az login` locally; user-assigned MI in
prod). HDInsight uses cluster Basic-auth creds — set `SparkHistory:HDInsight:Password` as a
Container App **secret**, not plaintext. The exact **Synapse** REST base differs from the
`ms.web.azuresynapse.net` UI link — adjust `Synapse:BaseUrlTemplate` to the endpoint that returns
JSON for your workspace.

## Identity (one shared credential)

All Azure auth — Foundry / prompt-agent, Azure OpenAI, and the Spark History Server — goes
through **one shared `TokenCredential`** registered in DI and selected by the `AzureIdentity`
section:

| Setting | Meaning |
|---|---|
| `ManagedIdentityClientId` | **User-assigned** Managed Identity client id (GUID). Empty = `DefaultAzureCredential` (system-assigned MI in Azure, `az login` locally). |

- **Local dev:** leave it empty and `az login`.
- **Prod, system-assigned MI:** leave it empty — `DefaultAzureCredential` picks up the MI.
- **Prod, user-assigned MI:** set `AzureIdentity:ManagedIdentityClientId` to the MI's client id
  (or the `AZURE_CLIENT_ID` env var). **No code change** — the same credential is used everywhere.

Grant that identity the roles it needs: **Foundry User** (call model + use agents) — or
**Foundry Project Manager** (also *publish* agents) for the `PromptAgent` provider —
on the Foundry project, and any scope needed to reach the Spark History Server.

## Run
```powershell
dotnet run --project .\src\Tools\SparkEventAnalyzer\SparkEventAnalyzer.csproj
```

## Deploy to Azure Container Apps

[deploy/deploy-aca.ps1](deploy/deploy-aca.ps1) provisions everything and deploys:
a **user-assigned Managed Identity** (the app's identity), an **ACR** (image built remotely
via `az acr build` — no local Docker), a **Container Apps environment**, and the **Container App**
with the UAMI attached, and grants the UAMI RBAC on the Foundry project.

```powershell
az login
./deploy/deploy-aca.ps1 `
  -ResourceGroup rg-spark -AcrName sparkacr$(Get-Random) -Provider Foundry `
  -FoundryProjectResourceId "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<account>/projects/<your-foundry-project>"
```

Notes:
- The UAMI is created by the script; its client id is injected into the app as
  `AzureIdentity__ManagedIdentityClientId` / `AZURE_CLIENT_ID` (so `appsettings.json` stays empty).
- Foundry's own system-assigned identity (the "Enterprise Application") is **not** used here —
  the app authenticates as the **UAMI**, which the script grants **Foundry User** on the
  project (or **Foundry Project Manager** when `-Provider PromptAgent`).
- The image restores from **nuget.org** ([nuget.docker.config](nuget.docker.config)) so the ACR
  build doesn't need the private corp feed. If a preview package is missing there, restore fails —
  fall back to the ADO feed with a build credential.
- For a full `/api/analyze` you must point the tool at a reachable History Server via
  `-SparkHistoryBaseUrl` (+ `-SparkHistoryAuthScope`); otherwise only `/health` and the
  model round-trip are exercised.

## Endpoints
| Method | Route | Purpose |
|---|---|---|
| GET | `/health` | Liveness |
| POST | `/api/analyze` | **Agent analysis** (LLM + `get_spark_run_summary` tool) |
| GET | `/api/spark/{appId}/summary?attemptId=` | **Compact run summary** (deterministic) |
| GET | `/api/spark/applications` | Raw app list |
| GET | `/api/spark/{appId}/jobs?attemptId=` | Raw jobs |
| GET | `/api/spark/{appId}/stages?attemptId=` | Raw stages |
| GET | `/api/spark/{appId}/executors?attemptId=` | Raw executors |

Example (deterministic summary):
```powershell
curl http://localhost:5280/api/spark/application_1700000000000_0001/summary
```

Example (agent analysis — requires Azure OpenAI configured):
```powershell
$body = @{ appId = "application_1700000000000_0001"; question = "Why did it fail?" } | ConvertTo-Json
Invoke-RestMethod -Uri http://localhost:5280/api/analyze -Method Post -Body $body -ContentType "application/json"
```

## Agent (Component 2)
`POST /api/analyze` runs the **Spark failure analyst** agent: an Azure OpenAI
chat-completion loop with automatic function calling. The LLM calls the
`get_spark_run_summary` tool (which runs the summarizer in-process), then explains
failures / perf issues and likely causes. This mirrors the Foundry Agent Service
"agent decides, backend executes" pattern and is swappable to Foundry later.

Configure Azure OpenAI in [appsettings.json](appsettings.json) → `AzureOpenAI`:

| Setting | Meaning | Example |
|---|---|---|
| `Endpoint` | Azure OpenAI endpoint | `https://my-aoai.openai.azure.com/` |
| `Deployment` | Model deployment name | `gpt-4o` / `gpt-5` |
| `ApiKey` | Optional; empty = use Managed Identity / `az login` | |

If `AzureOpenAI` is not configured, `/api/analyze` returns HTTP 503 with guidance;
the deterministic `/summary` endpoint still works.


## Next steps (from the plan)
- **Component 6:** return a structured analysis schema (`failures[]`, `likelyCauses[]`, `nextSteps[]`, `signals{}`, `citations[]`) from `/api/analyze`.
- **Component 5:** add ADO MCP → AI Search for code correlation (map failures/stack frames to code + recent PRs).
- **Component 4:** deepen `preprocess_events` by stream-parsing raw event-log files from storage (stack frames, SQL plan nodes).
- **Component 2 (prod runtime):** exercise `Agent:Provider=Foundry` against the Foundry project (`az login` + Foundry User RBAC + model deployment).
- **Component 1 (prod):** add Entra Easy Auth + deploy to Azure Container Apps with MI.
