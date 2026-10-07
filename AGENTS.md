# JobFinder engineering showcase

This is a sanitized source snapshot of a personal .NET 10 workflow system. Read README.md and docs/ARCHITECTURE.md first.

Use .NET SDK 10.0.401 or a compatible later SDK. Build with `dotnet build JobFinder.slnx -c Release`.
Tests are xUnit v3 using Microsoft.Testing.Platform. PostgreSQL integration tests use disposable Docker containers.
Do not run login, publishing, employer delivery, paid model probes, or production migrations as part of automated verification.
Never commit credentials, candidate documents, session data, generated resumes, or browser storage. Preserve source version witnesses,
transactional outbox boundaries, conservative unknown-delivery handling, quality checks and source-generated JSON contracts.

Development includes AI-assisted implementation. Architecture, constraints, verification and final review remain the author's responsibility.
