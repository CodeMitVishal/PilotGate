# ClaimsAssist as an EPAM DIAL application (AuthMode Dial). Build context: the repository root.
#   docker build -f src/ClaimsAssist.Api/Dockerfile -t claimsassist:1.0.0 .
# Model access: Microsoft Entra ID only (managed identity in Azure; no keys). Synthetic data is baked into the image.
FROM mcr.microsoft.com/dotnet/sdk@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS build
WORKDIR /src
COPY src/ClaimsAssist.Core/ClaimsAssist.Core.csproj src/ClaimsAssist.Core/
COPY src/ClaimsAssist.Api/ClaimsAssist.Api.csproj src/ClaimsAssist.Api/
RUN dotnet restore src/ClaimsAssist.Api/ClaimsAssist.Api.csproj
COPY src/ClaimsAssist.Core/ src/ClaimsAssist.Core/
COPY src/ClaimsAssist.Api/ src/ClaimsAssist.Api/
RUN dotnet publish src/ClaimsAssist.Api/ClaimsAssist.Api.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21
WORKDIR /app
COPY --from=build /out ./
COPY data/ ./data/
# Only the embedding cache and the audit log are written; everything else stays read-only for the app user.
RUN mkdir -p /app/data/.cache /app/logs && chown -R app:app /app/data/.cache /app/logs
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ClaimsAssist__AuthMode=Dial \
    ClaimsAssist__DataPath=data \
    ClaimsAssist__AuditLogPath=logs/claimsassist-audit.jsonl \
    DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "ClaimsAssist.Api.dll"]
