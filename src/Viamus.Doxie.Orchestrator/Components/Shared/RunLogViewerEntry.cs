namespace Viamus.Doxie.Orchestrator.Components.Shared;

public sealed record RunLogViewerEntry(
    int? Number,
    DateTimeOffset? Timestamp,
    string Text,
    string Kind = "stdout");
