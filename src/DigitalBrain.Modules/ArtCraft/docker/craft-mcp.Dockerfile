# syntax=docker/dockerfile:1
# Headless ArtCraft app (photocraft | filmcraft) as a Streamable-HTTP MCP server on :8000/mcp.
# The CLI speaks MCP over stdio; supergateway bridges it to HTTP so the brain's MCP connector
# reaches every server the same way.
ARG RUST_VERSION=1.99
FROM rust:${RUST_VERSION}-slim-bookworm AS builder
ARG APP=photocraft
ARG REF=main
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git pkg-config \
      libxkbcommon-dev libwayland-dev libx11-dev libxrandr-dev libxi-dev libxcursor-dev libasound2-dev libfontconfig1-dev \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /src
RUN git clone https://github.com/storytold/${APP}.git . && git checkout ${REF}
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/src/target,id=${APP}-target \
    cargo build --release --locked -p ${APP}-cli && cp target/release/${APP}-cli /usr/local/bin/craft-cli

FROM node:22-bookworm-slim
ARG APP=photocraft
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates libfontconfig1 libasound2 \
    && rm -rf /var/lib/apt/lists/* && npm install -g supergateway@4.1.0 && mkdir -p /work
COPY --from=builder /usr/local/bin/craft-cli /usr/local/bin/craft-cli
ENV CRAFT_APP=${APP}
VOLUME /work
EXPOSE 8000
CMD ["sh", "-c", "exec supergateway --stdio 'craft-cli mcp --automation-read-root /work --automation-write-root /work' --outputTransport streamableHttp --stateful --port 8000 --streamableHttpPath /mcp --healthEndpoint /healthz"]
