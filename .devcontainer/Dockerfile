FROM mcr.microsoft.com/devcontainers/dotnet:2-10.0-noble@sha256:9eb314432a5fa67773a53ab754cdc381cae614906162f9fc44cdb7f7837b91c7

USER root
ENV TZ=America/New_York

# Install system tools and GitHub CLI, then prepare the NuGet cache.
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        bubblewrap \
        ca-certificates \
        curl \
        lib32gcc-s1 \
        lib32stdc++6 \
        python3 \
        tzdata \
        unzip \
        xclip \
    && ln -snf /usr/share/zoneinfo/${TZ} /etc/localtime \
    && echo "${TZ}" > /etc/timezone \
    && mkdir -p -m 755 /etc/apt/keyrings \
    && curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && chmod 644 /etc/apt/keyrings/githubcli-archive-keyring.gpg \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends gh \
    && gh --version \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /home/vscode/.nuget/packages /home/vscode/.nuget/NuGet \
    && chown -R vscode:vscode /home/vscode/.nuget

USER vscode
ENV PATH="/home/vscode/.local/bin:${PATH}"

# Install Codex as vscode and prepare its runtime directories.
RUN mkdir -p -m 700 /home/vscode/.config/gh \
    && curl -fsSL https://chatgpt.com/codex/install.sh -o /tmp/codex-install.sh \
    && CODEX_NON_INTERACTIVE=1 sh /tmp/codex-install.sh \
    && CODEX_INSTALL_DAEMON_ONLY=1 CODEX_NON_INTERACTIVE=1 sh /tmp/codex-install.sh \
    && mkdir -p /home/vscode/.codex/app-server-daemon /home/vscode/.codex/app-server-control \
    && rm /tmp/codex-install.sh \
    && codex --version
