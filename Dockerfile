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

# /data is where Fly.io mounts the persistent volume — SQLite DB yahan rahega
RUN mkdir -p /data

COPY --from=build /app/publish .

# Fly.io port 8080 use karta hai by default
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AmpmHrmsPro.dll"]
