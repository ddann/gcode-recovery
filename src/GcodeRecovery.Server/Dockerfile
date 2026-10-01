# Build from the repository root:
#   docker build -f src/GcodeRecovery.Server/Dockerfile -t gcode-recovery-community .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/GcodeRecovery.Telemetry/ src/GcodeRecovery.Telemetry/
COPY src/GcodeRecovery.Server/ src/GcodeRecovery.Server/
RUN dotnet publish src/GcodeRecovery.Server -c Release -o /app -p:DebugType=None

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown app:app /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Community__Database=/data/community.db \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
USER app
EXPOSE 8080
VOLUME /data
ENTRYPOINT ["dotnet", "GcodeRecovery.Server.dll"]
