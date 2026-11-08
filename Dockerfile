# syntax=docker/dockerfile:1

# One build, two images: `--target api` (API + Hangfire server) and `--target simulator` (fictional market feeds).

# ---- build -------------------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore in its own layer: it only re-runs when package versions or project files change.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Raf.Core/Raf.Core.csproj src/Raf.Core/
COPY src/Raf.Simulation/Raf.Simulation.csproj src/Raf.Simulation/
COPY src/Raf.Api/Raf.Api.csproj src/Raf.Api/
COPY src/Raf.FeedSimulator/Raf.FeedSimulator.csproj src/Raf.FeedSimulator/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/Raf.Api/Raf.Api.csproj && dotnet restore src/Raf.FeedSimulator/Raf.FeedSimulator.csproj

COPY .editorconfig ./
COPY src/ src/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Raf.Api/Raf.Api.csproj --configuration Release --no-restore --output /app/api -p:UseAppHost=false \
 && dotnet publish src/Raf.FeedSimulator/Raf.FeedSimulator.csproj --configuration Release --no-restore --output /app/simulator -p:UseAppHost=false

# ---- runtime images ----------------------------------------------------------------------------------
# Chiseled Ubuntu: no shell, no package manager, and a non-root "app" user (UID 1654) baked in.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS simulator
LABEL org.opencontainers.image.title="raf-feed-simulator" \
      org.opencontainers.image.source="https://github.com/hekimcan206/raf" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/simulator .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Raf.FeedSimulator.dll"]

# Last stage = default target when no --target is given.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS api
LABEL org.opencontainers.image.title="raf-api" \
      org.opencontainers.image.source="https://github.com/hekimcan206/raf" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=build --link /app/api .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Raf.Api.dll"]
