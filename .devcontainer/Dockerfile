# syntax=docker/dockerfile:1
#
# Universal IntoChat OS agent dev-slot & cloud verification image (INT-2595).
# Compatible with local `agent-dev-slots` leased slots and Wave 2 ACA Consumption Jobs.
#
# Security invariants:
# - Unprivileged non-root user (`agent`, UID/GID 1000) with scrubbed `/home/agent`
# - Zero host credentials, zero SSH/Git/cloud configs, zero custody keys baked in
# - Exact toolchain parity with CI (.NET 11 SDK, Flutter 3.47.6, Playwright Chromium, git)

FROM mcr.microsoft.com/dotnet/sdk:11.0.100

ARG USER_NAME=agent
ARG USER_UID=1000
ARG USER_GID=1000
ARG FLUTTER_VERSION=3.47.6
ARG PLAYWRIGHT_VERSION=1.51.1

ENV DEBIAN_FRONTEND=noninteractive \
    FLUTTER_HOME=/opt/flutter \
    PLAYWRIGHT_BROWSERS_PATH=/opt/playwright \
    PATH="/opt/flutter/bin:/opt/flutter/bin/cache/dart-sdk/bin:${PATH}"

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        jq \
        libglu1-mesa \
        nodejs \
        npm \
        procps \
        unzip \
        xz-utils \
    && rm -rf /var/lib/apt/lists/*

# Install Flutter matching .github/workflows/ci.yml and nightly-e2e.yml.
RUN curl -fsSL "https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_${FLUTTER_VERSION}-stable.tar.xz" \
        | tar -xJ -C /opt \
    && git config --system --add safe.directory "${FLUTTER_HOME}" \
    && flutter --version \
    && flutter precache --web \
    && chmod -R a+rX "${FLUTTER_HOME}"

# Install Playwright Chromium and its OS dependencies into a shared read-only browser path.
RUN mkdir -p "${PLAYWRIGHT_BROWSERS_PATH}" \
    && npx --yes "playwright@${PLAYWRIGHT_VERSION}" install --with-deps chromium \
    && chmod -R a+rX "${PLAYWRIGHT_BROWSERS_PATH}" \
    && rm -rf /root/.npm /tmp/npm-* /var/lib/apt/lists/*

# Create unprivileged UID/GID 1000 `agent` user with a scrubbed ephemeral home layout.
RUN set -eux; \
    existing_group="$(getent group "${USER_GID}" | cut -d: -f1 || true)"; \
    if [ -n "${existing_group}" ] && [ "${existing_group}" != "${USER_NAME}" ]; then \
        groupmod -n "${USER_NAME}" "${existing_group}"; \
    elif [ -z "${existing_group}" ]; then \
        groupadd --gid "${USER_GID}" "${USER_NAME}"; \
    fi; \
    existing_user="$(getent passwd "${USER_UID}" | cut -d: -f1 || true)"; \
    if [ -n "${existing_user}" ] && [ "${existing_user}" != "${USER_NAME}" ]; then \
        usermod -l "${USER_NAME}" -d "/home/${USER_NAME}" -m "${existing_user}"; \
    elif [ -z "${existing_user}" ]; then \
        useradd --uid "${USER_UID}" --gid "${USER_GID}" --create-home --home-dir "/home/${USER_NAME}" --shell /bin/bash "${USER_NAME}"; \
    fi; \
    rm -rf "/home/${USER_NAME}"/.[!.]* "/home/${USER_NAME}"/*; \
    mkdir -p \
        "/home/${USER_NAME}/.config" \
        "/home/${USER_NAME}/.local/share" \
        "/home/${USER_NAME}/.cache" \
        "/home/${USER_NAME}/tmp" \
        "/home/${USER_NAME}/intochat-home" \
        /workspace; \
    chown -R "${USER_UID}:${USER_GID}" "/home/${USER_NAME}" /workspace "${FLUTTER_HOME}"

ENV HOME=/home/agent \
    XDG_CONFIG_HOME=/home/agent/.config \
    XDG_DATA_HOME=/home/agent/.local/share \
    XDG_CACHE_HOME=/home/agent/.cache \
    TMPDIR=/home/agent/tmp \
    DOTNET_CLI_HOME=/home/agent \
    DOTNET_NOLOGO=true \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true \
    GIT_CONFIG_NOSYSTEM=1 \
    GIT_CONFIG_GLOBAL=/dev/null

WORKDIR /workspace
USER agent
