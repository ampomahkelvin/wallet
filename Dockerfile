FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY wallet-ledger.sln ./
COPY src/Wallet.Domain/Wallet.Domain.csproj src/Wallet.Domain/
COPY src/Wallet.Infrastructure/Wallet.Infrastructure.csproj src/Wallet.Infrastructure/
COPY src/Wallet.Api/Wallet.Api.csproj src/Wallet.Api/
RUN dotnet restore src/Wallet.Api/Wallet.Api.csproj
COPY src/ src/
RUN dotnet build src/Wallet.Api/Wallet.Api.csproj -c Release --no-restore -o /app/build

FROM build AS publish
RUN dotnet publish src/Wallet.Api/Wallet.Api.csproj -c Release --no-restore -o /app/publish

FROM base AS final
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Wallet.Api.dll"]