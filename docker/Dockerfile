ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

COPY global.json ./
COPY src/Oficina.OrdensServico.Domain/Oficina.OrdensServico.Domain.csproj src/Oficina.OrdensServico.Domain/
COPY src/Oficina.OrdensServico.Application/Oficina.OrdensServico.Application.csproj src/Oficina.OrdensServico.Application/
COPY src/Oficina.OrdensServico.Infrastructure/Oficina.OrdensServico.Infrastructure.csproj src/Oficina.OrdensServico.Infrastructure/
COPY src/Oficina.OrdensServico.Api/Oficina.OrdensServico.Api.csproj src/Oficina.OrdensServico.Api/
RUN dotnet restore src/Oficina.OrdensServico.Api/Oficina.OrdensServico.Api.csproj

COPY . .
RUN dotnet publish src/Oficina.OrdensServico.Api/Oficina.OrdensServico.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Oficina.OrdensServico.Api.dll"]
