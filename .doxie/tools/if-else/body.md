# If / Else

Select an upstream JSON file and optionally a path within it. `source` defaults to `result.json`. Use `source=context` to read a single upstream transient JSON payload. If the default file is absent, a single available context can supply the value. Choose an operator such as `truthy`, `exists`, `equals` or `contains`. For operators that compare against a value, set `value` explicitly.

Label outgoing edges with `condition` values of `true` or `false`. The engine runs the selected branch and writes `decision.json` with the evaluation and reason.
