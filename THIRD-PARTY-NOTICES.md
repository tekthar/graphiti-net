# Third-party notices

## Graphiti

graphiti-net is a rewrite of Graphiti in C#. Files translated from Graphiti's Python source, prompts or tests keep
Graphiti's copyright line and name the upstream file and release they come from. They are listed here as they are
added.

Baseline: Graphiti v0.31.2.

| graphiti-net file | Upstream file |
| --- | --- |
| `src/GraphitiNet.Core/Errors.cs` | `graphiti_core/errors.py` |
| `src/GraphitiNet.Core/Models/Nodes.cs` | `graphiti_core/nodes.py` |
| `src/GraphitiNet.Core/Models/Edges.cs` | `graphiti_core/edges.py` |
| `src/GraphitiNet.Core/Utils/DateTimeUtils.cs` | `graphiti_core/utils/datetime_utils.py` |
| `src/GraphitiNet.Core/Utils/Validation.cs` | `graphiti_core/helpers.py` |
| `src/GraphitiNet.Core/Llm/TokenUsageTracker.cs` | `graphiti_core/llm_client/token_tracker.py` |
| `tests/GraphitiNet.Core.Tests/Llm/TokenUsageTrackerTests.cs` | `tests/llm_client/test_token_tracker.py` |
| `tests/GraphitiNet.Core.Tests/Models/NodeLabelSecurityTests.cs` | `tests/test_node_label_security.py` |

Source: https://github.com/getzep/graphiti/tree/v0.31.2

```
Copyright 2024, Zep Software, Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```
