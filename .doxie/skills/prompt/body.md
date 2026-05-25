# Prompt Skill

A small workflow primitive for applying a free-text prompt to upstream input and
writing a structured output file. Use it as glue between task-specific nodes
when a lightweight transformation is enough.

## Inputs

- `prompt`: instruction to apply.
- `input`: static text, upstream file, or upstream payload.
- `output_file`: output filename.
- `output_format`: `text`, `md`, or `json`.
- `output_schema`: optional JSON schema hint when `output_format=json`.

## Rules

- Follow the user-provided prompt.
- Keep the output focused on the requested transformation.
- Do not add domain-specific behavior to this primitive.
- Never include secrets or unrelated upstream content in the output.
