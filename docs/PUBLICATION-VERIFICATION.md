# Publication verification — 2026-10-07

Source: sanitized snapshot of private commit `2925785`. .NET SDK 10.0.401; Linux; disposable PostgreSQL containers.

| Check | Result | Meaning |
| --- | --- | --- |
| Whole-solution Release build | 0 warnings, 0 errors | The sanitized source compiles, including the actual Blazor application. |
| Core, LLM and recorded-source suites | 929 passed, 0 failed, 0 skipped | Domain rules, task/schema/routing doubles and page parsing still work after fixture script removal. |
| Wolverine compatibility suite | 10 passed, 0 failed, 0 skipped | Actual PostgreSQL transport/outbox seams, producer exit and process-termination recovery cases. |
| Publication scan | No confirmed credentials in tracked publication files | Manually classified remaining matches: a deterministic synthetic API hash, a C# parameter named linkedInOptions, and prose containing “business-key”. |
| UI artifacts | Synthetic acceptance screenshots | Images were retained from the 2026-10-05 isolated application-component acceptance; they are not new live account captures. |

**939 distinct tests were rerun for publication.** This is a selected suite, not the whole historical 3,236-test suite. No real provider quality, external delivery or production performance claim is made.

Reproduction:

```bash
dotnet build JobFinder.slnx -c Release
dotnet tests/JobFinder.Tests/bin/Release/net10.0/JobFinder.Tests.dll \
  -namespace JobFinder.Tests.Core -namespace JobFinder.Tests.Llm \
  -namespace JobFinder.Tests.Sources -maxThreads 4
dotnet tests/JobFinder.Tests/bin/Release/net10.0/JobFinder.Tests.dll \
  -class JobFinder.Tests.Pipeline.WolverineIntegrationTests -maxThreads 4
```

The initial source snapshot omits the personal channel seed list, private working history, operational handoff and research scratch files. Web fixtures retain their parser-relevant data but lose unnecessary executable script bodies. Credentials, accounts and candidate artifacts are not bundled.
