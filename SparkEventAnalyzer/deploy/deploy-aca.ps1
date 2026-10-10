<#
.SYNOPSIS
  Provisions and deploys the Spark Event Analyzer to Azure Container Apps with a
  user-assigned Managed Identity (UAMI).

.WHAT IT CREATES
  - Resource group (if missing)
  - User-assigned Managed Identity (the app's identity)
  - Azure Container Registry (ACR) + AcrPull for the UAMI
  - Container image (built remotely with `az acr build` — no local Docker needed)
  - Container Apps environment + the Container App (UAMI attached, ingress on 8080)
  - RBAC: grants the UAMI access to the Foundry project (if -FoundryProjectResourceId given)

.PREREQUISITES
  - `az login` as someone who can create these resources and assign roles
  - The Foundry project + gpt-5 deployment already exist
  - Required packages available on nuget.org (the image restores from nuget.org)

.EXAMPLE
  ./deploy-aca.ps1 -ResourceGroup rg-spark -AcrName sparkacr$((Get-Random)) `
     -FoundryProjectResourceId "/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<account>/projects/<your-foundry-project>"
#>
[CmdletBinding()]
param(
    [string] $SubscriptionId,
    [Parameter(Mandatory = $true)] [string] $ResourceGroup,
    [string] $Location = "eastus2",

    # Container Registry name must be globally unique, 5-50 alphanumerics, lowercase.
    [Parameter(Mandatory = $true)] [string] $AcrName,

    [string] $AcaEnv   = "spark-analyzer-env",
    [string] $AppName  = "spark-analyzer",
    [string] $UamiName = "spark-analyzer-mi",
    [string] $ImageTag = "v1",

    # Agent runtime to deploy: Foundry (default), PromptAgent, or AzureOpenAI.
    [ValidateSet("Foundry", "PromptAgent", "AzureOpenAI")]
    [string] $Provider = "Foundry",

    [string] $FoundryProjectEndpoint = "https://<your-ai-services>.services.ai.azure.com/api/projects/<your-foundry-project>",
    [string] $FoundryDeployment      = "gpt-5",

    # Resource id of the Foundry PROJECT (or its parent account) — RBAC scope for the UAMI.
    # Leave empty to skip role assignment (you'll be told how to do it manually).
    [string] $FoundryProjectResourceId,

    # Spark History defaults. Platform + cluster/workspace are usually supplied PER REQUEST, but
    # these set the fallback Generic base and (if used) HDInsight gateway Basic-auth creds.
    [string] $SparkHistoryGenericBaseUrl,
    [string] $HdInsightUsername,
    [string] $HdInsightPassword
)

$ErrorActionPreference = "Stop"
$projectDir = Split-Path $PSScriptRoot -Parent   # ...\SparkEventAnalyzer (Docker build context)

function Say($m) { Write-Host "==> $m" -ForegroundColor Cyan }

if ($SubscriptionId) { az account set --subscription $SubscriptionId | Out-Null }

Say "Registering providers + Container Apps CLI extension"
az extension add --name containerapp --upgrade --only-show-errors | Out-Null
az provider register -n Microsoft.App --wait | Out-Null
az provider register -n Microsoft.OperationalInsights --wait | Out-Null
az provider register -n Microsoft.ContainerRegistry --wait | Out-Null

Say "Resource group: $ResourceGroup ($Location)"
az group create -n $ResourceGroup -l $Location --only-show-errors | Out-Null

Say "User-assigned Managed Identity: $UamiName"
az identity create -g $ResourceGroup -n $UamiName --only-show-errors | Out-Null
$uami = az identity show -g $ResourceGroup -n $UamiName -o json | ConvertFrom-Json
$uamiId        = $uami.id
$uamiClientId  = $uami.clientId
$uamiPrincipal = $uami.principalId
Say "UAMI clientId=$uamiClientId principalId=$uamiPrincipal"

Say "Container Registry: $AcrName"
az acr create -g $ResourceGroup -n $AcrName --sku Basic --only-show-errors | Out-Null
$acrId    = az acr show -n $AcrName --query id -o tsv
$acrLogin = az acr show -n $AcrName --query loginServer -o tsv

Say "Grant AcrPull to the UAMI on the registry"
az role assignment create --assignee-object-id $uamiPrincipal --assignee-principal-type ServicePrincipal `
    --role "AcrPull" --scope $acrId --only-show-errors | Out-Null

Say "Build image remotely in ACR: $acrLogin/$AppName:$ImageTag"
az acr build -r $AcrName -t "${AppName}:${ImageTag}" $projectDir

# Foundry RBAC. Use role DEFINITION IDs, not names — the Foundry roles were renamed
# (Azure AI User -> Foundry User, Azure AI Project Manager -> Foundry Project Manager) and the
# old "Azure AI Developer" role does NOT grant Foundry project/agent access.
#   Foundry User            53ca6127-db72-4b80-b1b0-d745d6d5456d  (call model + use agents)
#   Foundry Project Manager eadc314b-1a2d-4efa-be10-5d325db5065e  (also PUBLISH/create agents)
$foundryUserRoleId    = "53ca6127-db72-4b80-b1b0-d745d6d5456d"
$foundryPmRoleId      = "eadc314b-1a2d-4efa-be10-5d325db5065e"
if ($FoundryProjectResourceId) {
    if ($Provider -eq "PromptAgent") {
        Say "PromptAgent selected -> grant Foundry Project Manager (publish agents)"
        az role assignment create --assignee-object-id $uamiPrincipal --assignee-principal-type ServicePrincipal `
            --role $foundryPmRoleId --scope $FoundryProjectResourceId --only-show-errors | Out-Null
    }
    else {
        Say "Grant Foundry User to the UAMI (call model + use agents)"
        az role assignment create --assignee-object-id $uamiPrincipal --assignee-principal-type ServicePrincipal `
            --role $foundryUserRoleId --scope $FoundryProjectResourceId --only-show-errors | Out-Null
    }
}
else {
    $roleId = if ($Provider -eq "PromptAgent") { $foundryPmRoleId } else { $foundryUserRoleId }
    Write-Warning "No -FoundryProjectResourceId given. Assign RBAC manually:"
    Write-Warning "  az role assignment create --assignee-object-id $uamiPrincipal --assignee-principal-type ServicePrincipal --role $roleId --scope <foundry-project-or-account-resource-id>"
}

Say "Container Apps environment: $AcaEnv"
az containerapp env create -g $ResourceGroup -n $AcaEnv -l $Location --only-show-errors | Out-Null

# Build env var list (double-underscore = nested config key). AZURE_CLIENT_ID also helps
# DefaultAzureCredential resolve the user-assigned identity.
$envVars = @(
    "Agent__Provider=$Provider",
    "Foundry__ProjectEndpoint=$FoundryProjectEndpoint",
    "Foundry__Deployment=$FoundryDeployment",
    "AzureIdentity__ManagedIdentityClientId=$uamiClientId",
    "AZURE_CLIENT_ID=$uamiClientId"
)
if ($SparkHistoryGenericBaseUrl) { $envVars += "SparkHistory__Generic__BaseUrl=$SparkHistoryGenericBaseUrl" }
if ($HdInsightUsername)          { $envVars += "SparkHistory__HDInsight__Username=$HdInsightUsername" }
if ($HdInsightPassword)          { $envVars += "SparkHistory__HDInsight__Password=$HdInsightPassword" }

Say "Deploy Container App: $AppName (UAMI-attached, pull via UAMI, ingress :8080)"
az containerapp create -g $ResourceGroup -n $AppName --environment $AcaEnv `
    --image "$acrLogin/${AppName}:${ImageTag}" `
    --user-assigned $uamiId `
    --registry-server $acrLogin --registry-identity $uamiId `
    --ingress external --target-port 8080 `
    --min-replicas 1 --max-replicas 1 `
    --env-vars $envVars `
    --only-show-errors | Out-Null

$fqdn = az containerapp show -g $ResourceGroup -n $AppName --query properties.configuration.ingress.fqdn -o tsv
Say "Deployed. https://$fqdn"
Say "Verify:  Invoke-RestMethod https://$fqdn/health"
Write-Host ""
Write-Host "Analyze (platform + cluster/workspace are provided per request):" -ForegroundColor Yellow
Write-Host "  # HDInsight:"
Write-Host "  `$b = @{ platform='HDInsight'; cluster='<cluster>.azurehdinsight.net'; appId='application_...'; attemptId='1'; question='Why did it fail?' } | ConvertTo-Json"
Write-Host "  Invoke-RestMethod https://$fqdn/api/analyze -Method Post -Body `$b -ContentType application/json"
