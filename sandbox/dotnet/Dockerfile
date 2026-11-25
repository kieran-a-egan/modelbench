FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Runner/ .
RUN dotnet publish Runner.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/runtime:10.0
COPY --from=build /out /runner
USER 65534:65534
ENV DOTNET_EnableDiagnostics=0 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
ENTRYPOINT ["dotnet", "/runner/Runner.dll"]
