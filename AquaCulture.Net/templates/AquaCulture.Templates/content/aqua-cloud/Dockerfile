# Multi-tenant AquaCulture.Net API. Tenant databases live in /data (mount a volume).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish AquaCloud.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV Aqua__DataRoot=/data ASPNETCORE_URLS=http://+:8080
VOLUME /data
EXPOSE 8080
# AQUA_ADMIN_KEY (≥ 16 chars) enables /admin/tenants; AQUA_LLM_* enables an LLM for /ask
ENTRYPOINT ["dotnet", "AquaCloud.dll"]
