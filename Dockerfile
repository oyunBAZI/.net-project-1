# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY AxiomLedger.sln ./
COPY src/AxiomLedger.Domain/AxiomLedger.Domain.csproj src/AxiomLedger.Domain/
COPY src/AxiomLedger.Application/AxiomLedger.Application.csproj src/AxiomLedger.Application/
COPY src/AxiomLedger.Infrastructure/AxiomLedger.Infrastructure.csproj src/AxiomLedger.Infrastructure/
COPY src/AxiomLedger.Web/AxiomLedger.Web.csproj src/AxiomLedger.Web/
COPY src/AxiomLedger.Cli/AxiomLedger.Cli.csproj src/AxiomLedger.Cli/
RUN dotnet restore src/AxiomLedger.Web/AxiomLedger.Web.csproj

COPY . .
RUN dotnet publish src/AxiomLedger.Web/AxiomLedger.Web.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN useradd --create-home --uid 10001 axiom && mkdir -p /data && chown -R axiom:axiom /data
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Default="Data Source=/data/axiomledger.db"

VOLUME ["/data"]
EXPOSE 8080
USER axiom

ENTRYPOINT ["dotnet", "AxiomLedger.Web.dll"]
