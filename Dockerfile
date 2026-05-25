# syntax=docker/dockerfile:1

ARG DOTNET_SDK_VERSION=10.0.103
ARG DOTNET_RUNTIME_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /src

COPY . .
RUN dotnet publish src/Viamus.Doxie.Orchestrator/Viamus.Doxie.Orchestrator.csproj \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION} AS runtime
WORKDIR /workspace

ENV ASPNETCORE_URLS=http://+:5034 \
    ASPNETCORE_ENVIRONMENT=Docker \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app/publish /app

EXPOSE 5034
ENTRYPOINT ["dotnet", "/app/Viamus.Doxie.Orchestrator.dll"]
