# syntax=docker/dockerfile:1

# The agent and the console client, built from the repository root for amd64 and arm64:
#   docker buildx build -f amneziageo-linux/docker/Dockerfile --platform linux/amd64,linux/arm64 .

ARG DOTNET=10.0

FROM --platform=$BUILDPLATFORM golang:1.25 AS engine
ARG TARGETARCH
WORKDIR /src
COPY amneziageo-android/amneziawg-go/ ./
RUN CGO_ENABLED=0 GOOS=linux GOARCH=$TARGETARCH go build -trimpath -ldflags "-s -w" -o /out/amneziawg-go .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:$DOTNET AS agent
ARG TARGETARCH
ARG VERSION=0.0.1
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY amneziageo-lib/ amneziageo-lib/
COPY amneziageo-linux/ amneziageo-linux/
COPY amneziageo-android/amneziawg-go/go.mod amneziageo-android/amneziawg-go/
COPY --from=engine /out/amneziawg-go amneziageo-android/amneziawg-go/
RUN case "$TARGETARCH" in \
      amd64) rid=linux-x64 ;; \
      arm64) rid=linux-arm64 ;; \
      *) echo "no build for $TARGETARCH" >&2; exit 1 ;; \
    esac \
 && for project in AmneziaGeo.Linux.App AmneziaGeo.Linux.Cli; do \
      dotnet publish "amneziageo-linux/$project/$project.csproj" -c Release -r "$rid" --self-contained false \
        -p:Version="$VERSION" -o /out --nologo -v minimal || exit 1; \
    done

FROM mcr.microsoft.com/dotnet/runtime:$DOTNET
RUN apt-get update \
 && apt-get install -y --no-install-recommends iproute2 nftables tini tzdata \
 && rm -rf /var/lib/apt/lists/*
COPY --from=agent /out/ /opt/amneziageo/
COPY --chmod=755 amneziageo-linux/docker/entrypoint.sh /usr/local/bin/amneziageo-entrypoint
COPY --chmod=755 amneziageo-linux/docker/bootstrap.sh /usr/local/bin/amneziageo-bootstrap
RUN ln -s /opt/amneziageo/amneziageo /usr/local/bin/amneziageo \
 && mkdir -p /etc/amneziageo/configs
ENV AMNEZIAGEO_DATA=/var/lib/amneziageo \
    AMNEZIAGEO_IFACE=awg0
VOLUME ["/var/lib/amneziageo"]
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s CMD amneziageo status > /dev/null || exit 1
ENTRYPOINT ["tini", "--", "amneziageo-entrypoint"]
