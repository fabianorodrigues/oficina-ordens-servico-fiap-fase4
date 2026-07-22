FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore Oficina.OrdensServico.sln
RUN dotnet publish src/Oficina.OrdensServico.Api/Oficina.OrdensServico.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ADD https://truststore.pki.rds.amazonaws.com/global/global-bundle.pem /tmp/aws-rds-global-bundle.pem
RUN cat /tmp/aws-rds-global-bundle.pem >> /etc/ssl/certs/ca-certificates.crt \
    && rm /tmp/aws-rds-global-bundle.pem
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "Oficina.OrdensServico.Api.dll"]
