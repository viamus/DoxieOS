# Loop Skill

A built-in workflow primitive. When placed inside a workflow, it iterates over
an array-shaped upstream payload and runs its body subgraph once per element.
It does not invoke a model directly; the runner handles iteration in-process.

## When To Use

- An upstream node produces a JSON array.
- The same recipe should run once per element.
- The per-item results should be collected into one downstream payload.

## Modes

- `iterate`
  - `array_source`: filename in the upstream output folder. Default
    `result.json`.
  - `array_path`: dotted JSON path to the array. Empty means the file root is
    the array.
  - `concurrency`: parallel iteration cap. Default `4`.
  - `on_failure`: `fail-fast` or `continue`.

## Output

The loop writes `results.json`, a JSON array containing each successful body
iteration's output summary.
