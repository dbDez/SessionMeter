# Graph Report - SessionMeter  (2026-07-12)

## Corpus Check
- 33 files · ~70,248 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 221 nodes · 305 edges · 23 communities (13 shown, 10 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `43decc00`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 10|Community 10]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 14|Community 14]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]

## God Nodes (most connected - your core abstractions)
1. `ContextWindowResolverTests` - 17 edges
2. `PiContextMonitor` - 13 edges
3. `ContextMonitor` - 12 edges
4. `OAuthUsageSource` - 11 edges
5. `ContextWindowResolver` - 9 edges
6. `ContextMonitorTests` - 9 edges
7. `SessionMeter — Setup, Build & Wiring Guide` - 9 edges
8. `OAuthUsageSourceTests` - 8 edges
9. `ContextReading` - 7 edges
10. `2. Wire it into a Claude Code session` - 7 edges

## Surprising Connections (you probably didn't know these)
- `PiContextMonitor` --references--> `MeterConfig`  [EXTRACTED]
  src/SessionMeter.Core/Context/PiContextMonitor.cs → src/SessionMeter.Core/Configuration/MeterConfig.cs
- `OAuthUsageSource` --references--> `MeterConfig`  [EXTRACTED]
  src/SessionMeter.Core/Usage/OAuthUsageSource.cs → src/SessionMeter.Core/Configuration/MeterConfig.cs
- `ContextMonitor` --references--> `MeterConfig`  [EXTRACTED]
  src/SessionMeter.Core/Context/ContextMonitor.cs → src/SessionMeter.Core/Configuration/MeterConfig.cs

## Import Cycles
- None detected.

## Communities (23 total, 10 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.08
Nodes (19): CancellationToken, HttpClient, IReadOnlyList, CredentialsFile, JsonSerializerOptions, Task, LimitDto, ModelDto (+11 more)

### Community 1 - "Community 1"
Cohesion: 0.09
Nodes (20): SessionMeter examples, Set up in 3 steps, 1. Install (end users), 2. Wire it into a Claude Code session, 3. Wire it into Pi, 4. Build the installer from source, 5. How the PATH entry works, 6. How the app icon is set (+12 more)

### Community 2 - "Community 2"
Cohesion: 0.15
Nodes (7): MeterConfig, ContextMonitor, DateTimeOffset, IEnumerable, JsonElement, UsageScan, ContextReading

### Community 4 - "Community 4"
Cohesion: 0.24
Nodes (6): DateTimeOffset, IEnumerable, JsonElement, PiContextMonitor, PiTranscript, PiUsageScan

### Community 5 - "Community 5"
Cohesion: 0.16
Nodes (9): Microsoft.NET.Test.Sdk (18.5.1), xunit (2.9.3), xunit.runner.visualstudio (3.1.5), net10.0, Microsoft.NET.Sdk, net10.0, Microsoft.NET.Sdk, net10.0 (+1 more)

### Community 6 - "Community 6"
Cohesion: 0.24
Nodes (4): InlineData, ContextMonitorTests, Fact, Theory

### Community 7 - "Community 7"
Cohesion: 0.30
Nodes (4): long, ContextWindowResolver, JsonElement, WindowResolution

### Community 8 - "Community 8"
Cohesion: 0.29
Nodes (4): string, Fact, Task, OAuthUsageSourceTests

### Community 9 - "Community 9"
Cohesion: 0.25
Nodes (7): Build & run, Context-window detection, Layout, Provenance, `session context`, Session — kill Claude Code's context guessing, `session usage`

### Community 10 - "Community 10"
Cohesion: 0.29
Nodes (6): Decisions made, First action next time, HandOff — <project> — <YYYY-MM-DD>, Lessons learned, Outstanding, What was done

## Knowledge Gaps
- **43 isolated node(s):** `net10.0`, `Microsoft.NET.Sdk`, `UsageDto`, `ScopeDto`, `ModelDto` (+38 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **10 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `MeterConfig` connect `Community 2` to `Community 0`, `Community 4`?**
  _High betweenness centrality (0.063) - this node is a cross-community bridge._
- **Why does `OAuthUsageSource` connect `Community 0` to `Community 2`?**
  _High betweenness centrality (0.061) - this node is a cross-community bridge._
- **Why does `PiContextMonitor` connect `Community 4` to `Community 2`?**
  _High betweenness centrality (0.040) - this node is a cross-community bridge._
- **What connects `net10.0`, `Microsoft.NET.Sdk`, `UsageDto` to the rest of the system?**
  _43 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.0846774193548387 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.09090909090909091 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.1471861471861472 - nodes in this community are weakly interconnected._