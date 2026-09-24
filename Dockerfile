# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# No git repo / local tools in the image - skip the Husky.Net hook install target.
ENV HUSKY=0
WORKDIR /src
COPY src/ObservabilityLab/ObservabilityLab.csproj src/ObservabilityLab/
RUN dotnet restore src/ObservabilityLab/ObservabilityLab.csproj
COPY src/ObservabilityLab/ src/ObservabilityLab/
RUN dotnet publish src/ObservabilityLab/ObservabilityLab.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
# No curl in the base image - a tiny HTTP request over bash /dev/tcp is enough.
HEALTHCHECK --interval=10s --timeout=3s --start-period=20s --retries=3 \
  CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/ready HTTP/1.0\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "ObservabilityLab.dll"]
