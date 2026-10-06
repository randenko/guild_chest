FROM mcr.microsoft.com/devcontainers/dotnet:2-10.0-noble@sha256:9eb314432a5fa67773a53ab754cdc381cae614906162f9fc44cdb7f7837b91c7

USER root
RUN apt-get update && apt-get install -y --no-install-recommends \
    lib32gcc-s1 lib32stdc++6 python3 unzip curl ca-certificates \
    && apt-get clean && rm -rf /var/lib/apt/lists/*
RUN mkdir -p /home/vscode/.nuget/packages /home/vscode/.nuget/NuGet \
    && chown -R vscode:vscode /home/vscode/.nuget
USER vscode
