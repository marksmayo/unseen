# Unseen dedicated server.
#
# The Unity build itself happens outside this image (see Server/build-server.sh), because building
# needs a licensed Unity editor. This image only packages the resulting Linux headless player, so it
# stays small and fast to roll out - which matters when a fleet is scaling on match demand.
FROM ubuntu:24.04

ARG BUILD_DIR=out/linux

ENV DEBIAN_FRONTEND=noninteractive \
    UNSEEN_PORT=7770 \
    UNSEEN_ENTITIES=64 \
    UNSEEN_SEED=20260824

# Unity headless players still link against a small set of system libraries.
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        libc6 \
        libstdc++6 \
        libgcc-s1 \
        zlib1g \
    && rm -rf /var/lib/apt/lists/*

RUN useradd --create-home --shell /usr/sbin/nologin unseen

WORKDIR /opt/unseen
COPY ${BUILD_DIR}/ /opt/unseen/
COPY entrypoint.sh /usr/local/bin/entrypoint.sh

RUN chmod +x /usr/local/bin/entrypoint.sh /opt/unseen/unseen-server \
    && chown -R unseen:unseen /opt/unseen

USER unseen

EXPOSE 7770/udp
EXPOSE 7770/tcp

# Agones and most orchestrators health-check the game port; a plain process check is enough here.
HEALTHCHECK --interval=15s --timeout=3s --start-period=20s --retries=3 \
    CMD pgrep -f unseen-server > /dev/null || exit 1

ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
