# Loop

Read `array_source` from a direct upstream node. The default source is `result.json`. Use `array_source=context` to read a single upstream transient JSON payload. If the default file is absent, a single available context can supply the input. Set `array_path` when the JSON array is nested. Configure `concurrency` to bound simultaneous iterations.

Tag each body node with the loop node ID in its `loopId`. Body agents receive the current item as an explicit `loop-item` source in their handoff. They receive only the artifacts and context available to their own step. Use `on_failure=continue` to collect successful iterations and record individual failures. The default `fail-fast` policy stops after an iteration fails.
