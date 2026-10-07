# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# NuGet packages live in a BuildKit cache mount that outlives every build: after the first build a code change compiles against the
# cached packages instead of downloading ~1 GB from nuget.org again (slow and flaky from here). Audit is off inside the image build:
# it is a network call on every restore, and the host build already reports vulnerable packages.
ENV NUGET_PACKAGES=/nuget \
    NUGET_ENABLE_ENHANCED_HTTP_RETRY=true \
    NUGET_ENHANCED_MAX_NETWORK_TRY_COUNT=8 \
    NUGET_ENHANCED_NETWORK_RETRY_DELAY_MILLISECONDS=2000 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
# Restore runs with the sources present: the Web SDK adds the Blazor framework assets (blazor.web.js) only when it sees .razor files,
# so a restore from the .csproj files alone publishes a dashboard without its script (static pages, nothing interactive). The package
# cache mount keeps the restore at a few seconds anyway.
COPY global.json Directory.Build.props JobFinder.slnx ./
COPY src/ src/
COPY config/appsettings.json config/
COPY prompts/ prompts/
COPY assets/ assets/
# linux-x64 only: no Playwright drivers for Windows and macOS in the output. The driver is moved aside for its own layer below.
# The build fails if the dashboard script is missing from the output.
RUN --mount=type=cache,id=jobfinder-nuget,target=/nuget \
    dotnet publish src/JobFinder.App -c Release -r linux-x64 --self-contained false -p:NuGetAudit=false -o /out \
    && test -f /out/wwwroot/_framework/blazor.web.js \
    && mv /out/.playwright /playwright

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV JOBFINDER_HOME=/app \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DOTNET_gcServer=0
# Chromium and its system libraries for Playwright (hh.ru responses, Threads, PDF rendering). Installed from the driver alone, before
# the app is copied: these layers depend only on the Playwright version, so a code change neither reinstalls the browser nor re-exports
# the ~100 MB driver.
COPY --from=build /playwright ./.playwright
RUN node=$(ls -d .playwright/node/*/node | head -1) \
    && "$node" .playwright/package/cli.js install --with-deps chromium \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /out ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "JobFinder.App.dll"]
