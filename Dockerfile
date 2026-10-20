FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ProxyCraft.csproj packages.lock.json global.json ./
COPY Build/ Build/
RUN dotnet restore ProxyCraft.csproj --locked-mode
COPY . .
RUN dotnet publish ProxyCraft.csproj --configuration Release --no-restore \
    --no-self-contained -p:UseAppHost=false --output /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Storage__Directory=/var/lib/proxycraft \
    DataProtection__KeysDirectory=/var/lib/proxycraft/keys \
    Http__RedirectToHttps=false
RUN mkdir -p /var/lib/proxycraft && chown app:app /var/lib/proxycraft
COPY --from=build /out/ ./
USER app
EXPOSE 8080/tcp 25565/tcp
ENTRYPOINT ["dotnet", "ProxyCraft.dll"]
