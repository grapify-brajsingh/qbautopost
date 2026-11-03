# Linux container for local integration testing with the dashboard (deploy/integration/docker-compose.yml).
# QuickBooks Desktop needs Windows COM, so the container runs the simulated company (QuickBooks:Fake=true), which the
# app allows only in Development/Testing. Production stays the Windows service of docs/runbook.md.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props QbAutopost.sln ./
COPY src/QbAutopost.Core/QbAutopost.Core.csproj src/QbAutopost.Core/
COPY src/QbAutopost.Data/QbAutopost.Data.csproj src/QbAutopost.Data/
COPY src/QbAutopost.QuickBooks/QbAutopost.QuickBooks.csproj src/QbAutopost.QuickBooks/
COPY src/QbAutopost.Api/QbAutopost.Api.csproj src/QbAutopost.Api/
RUN dotnet restore src/QbAutopost.Api/QbAutopost.Api.csproj
COPY src/ src/
COPY samples/rules.json samples/rules.json
RUN dotnet publish src/QbAutopost.Api/QbAutopost.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
RUN apt-get update && apt-get install -y --no-install-recommends tzdata curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY samples/rules.json /data/rules.json
RUN mkdir -p /data/logs /data/api-batches /data/jobs && chown -R app:app /data
USER app
ENV ASPNETCORE_ENVIRONMENT=Development \
    QuickBooks__Fake=true \
    Api__Bind=http://0.0.0.0:5080 \
    Api__AllowInsecureRemote=true \
    Company__RulesFile=/data/rules.json \
    Paths__Ledger=/data/ledger.json \
    Paths__QbLists=/data/qb-lists.json \
    Paths__Logs=/data/logs \
    Paths__JobIndex=/data/jobs.json \
    Paths__ApiBatches=/data/api-batches \
    Paths__AllowedJobRoots__0=/data/jobs \
    Paths__Clients=/data/clients.json \
    Company__FilePath=/data/company.qbw \
    Dashboard__Enabled=false
EXPOSE 5080
ENTRYPOINT ["dotnet", "QbAutopost.Api.dll"]
