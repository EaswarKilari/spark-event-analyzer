# syntax=docker/dockerfile:1
# Build context = this folder (SparkEventAnalyzer). The project is isolated
# (its own Directory.Build.* / Directory.Packages.props), so no repo-root files are needed.
#
# Restore uses nuget.docker.config (nuget.org) so an ACR remote build does NOT need the
# private corp ADO feed. If a required preview package is missing on nuget.org, restore
# fails here — see deploy/deploy-aca.ps1 for the ADO-feed fallback note.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first (better layer caching): copy csproj + isolation files + nuget config.
COPY nuget.docker.config ./NuGet.config
COPY SparkEventAnalyzer.csproj Directory.Build.props Directory.Build.targets Directory.Packages.props ./
RUN dotnet restore SparkEventAnalyzer.csproj

# Copy the rest and publish.
COPY . .
RUN dotnet publish SparkEventAnalyzer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Azure Container Apps routes to this port (set --target-port 8080 at deploy).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SparkEventAnalyzer.dll"]
