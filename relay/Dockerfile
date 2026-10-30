# Build and run the RemoteCraft relay in Docker (from the repository root):
#   docker build -f relay/Dockerfile -t remotecraft-relay .
#   docker run -d --name remotecraft-relay --restart unless-stopped -p 7180:7180 \
#     -e RCRELAY_KEY=change-me -v remotecraft-relay:/data remotecraft-relay
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/RemoteCraft.Relay/ ./
RUN dotnet publish RemoteCraft.Relay.csproj -c Release -o /app -p:DebugType=none

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app ./
ENV RCRELAY_PORT=7180 RCRELAY_DATA=/data
RUN mkdir /data && chown app /data
VOLUME /data
EXPOSE 7180
USER app
ENTRYPOINT ["dotnet", "RemoteCraft.Relay.dll"]
