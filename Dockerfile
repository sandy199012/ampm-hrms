# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore packages first (layer cache — only re-runs when .csproj changes)
COPY *.csproj .
RUN dotnet restore

# Copy rest of source and publish
COPY . .
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

RUN mkdir -p /data

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
# Use polling instead of inotify — avoids Linux container inotify limit errors
ENV DOTNET_USE_POLLING_FILE_WATCHER=1

EXPOSE 8080

ENTRYPOINT ["dotnet", "AmpmHrmsPro.dll"]
