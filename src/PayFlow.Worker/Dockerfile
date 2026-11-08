# syntax=docker/dockerfile:1
# Build context: repository root.  docker build -f src/PayFlow.Worker/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/PayFlow.Domain/PayFlow.Domain.csproj src/PayFlow.Domain/
COPY src/PayFlow.Contracts/PayFlow.Contracts.csproj src/PayFlow.Contracts/
COPY src/PayFlow.Application/PayFlow.Application.csproj src/PayFlow.Application/
COPY src/PayFlow.Infrastructure/PayFlow.Infrastructure.csproj src/PayFlow.Infrastructure/
COPY src/PayFlow.Worker/PayFlow.Worker.csproj src/PayFlow.Worker/
RUN dotnet restore src/PayFlow.Worker/PayFlow.Worker.csproj
COPY src/ src/
RUN dotnet publish src/PayFlow.Worker/PayFlow.Worker.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080     DOTNET_RUNNING_IN_CONTAINER=true     DOTNET_gcServer=0     Logging__Console__FormatterName=json
COPY --from=build /app/publish .
# Non-root user provided by the official .NET images.
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "PayFlow.Worker.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "PayFlow.Worker.dll"]
