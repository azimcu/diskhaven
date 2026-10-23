# HomeVault: web UI (Vite) + API (ASP.NET Core) in one small image.
FROM node:22-alpine AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY src/HomeVault.Core/HomeVault.Core.csproj src/HomeVault.Core/
COPY src/HomeVault.Api/HomeVault.Api.csproj src/HomeVault.Api/
RUN dotnet restore src/HomeVault.Api/HomeVault.Api.csproj
COPY src/ src/
RUN dotnet publish src/HomeVault.Api/HomeVault.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /out ./
COPY --from=web /web/dist ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=8080 \
    HomeVault__DataRoot=/data \
    HomeVault__ConfigDir=/config \
    DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "HomeVault.Api.dll"]
