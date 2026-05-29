using Microsoft.AspNetCore.Components;
using System.Text.Encodings.Web;

namespace MudBlazor;

public static class DoxieSnackbarExtensions
{
    private static readonly string[] MessageSeparators = [": ", " - ", " \u2014 ", " \u2013 "];

    public static void AddDoxieToast(
        this ISnackbar snackbar,
        string message,
        Severity severity = Severity.Normal,
        Action<SnackbarOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(snackbar);

        var text = SplitMessage(message);
        var markup = new MarkupString(BuildToastMarkup(text.Title, text.Body, chip: null, SignalFor(severity)));
        snackbar.AddDoxieToast(markup, severity, configure);
    }

    public static void AddDoxieToast(
        this ISnackbar snackbar,
        MarkupString markup,
        Severity severity = Severity.Normal,
        Action<SnackbarOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(snackbar);

        snackbar.Add(markup, severity, options =>
        {
            options.Icon = IconFor(severity);
            options.SnackbarTypeClass = $"doxie-toast-snackbar {ToastSeverityClass(severity)}";
            options.ShowTransitionDuration = 160;
            options.VisibleStateDuration = severity == Severity.Error ? 4200 : 3000;
            options.HideTransitionDuration = 220;
            options.ShowCloseIcon = false;
            options.RequireInteraction = false;
            options.CloseAfterNavigation = true;
            configure?.Invoke(options);
        });
    }

    private static ToastText SplitMessage(string? message)
    {
        var clean = Compact(message, 260);
        if (string.IsNullOrWhiteSpace(clean))
        {
            return new ToastText("DoxieOS", string.Empty);
        }

        foreach (var separator in MessageSeparators)
        {
            var separatorIndex = clean.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex is > 5 and < 64 && separatorIndex + separator.Length < clean.Length)
            {
                return new ToastText(clean[..separatorIndex], clean[(separatorIndex + separator.Length)..]);
            }
        }

        if (clean.Length > 86)
        {
            return new ToastText(Compact(clean, 86), clean);
        }

        return new ToastText(clean, string.Empty);
    }

    private static string BuildToastMarkup(string title, string? body, string? chip, string signal)
    {
        var encodedTitle = HtmlEncoder.Default.Encode(title);
        var encodedBody = HtmlEncoder.Default.Encode(body ?? string.Empty);
        var encodedSignal = HtmlEncoder.Default.Encode(signal);
        var chipMarkup = string.IsNullOrWhiteSpace(chip)
            ? string.Empty
            : $"<span class=\"doxie-toast-chip\">{HtmlEncoder.Default.Encode(chip)}</span>";
        var bodyMarkup = string.IsNullOrWhiteSpace(encodedBody)
            ? string.Empty
            : $"<div class=\"doxie-toast-body\">{encodedBody}</div>";

        return $"""
            <div class="doxie-toast">
                <div class="doxie-toast-topline">
                    <span class="doxie-toast-title">{encodedTitle}</span>
                    {chipMarkup}
                </div>
                {bodyMarkup}
                <div class="doxie-toast-foot">{encodedSignal}</div>
            </div>
            """;
    }

    private static string SignalFor(Severity severity) => severity switch
    {
        Severity.Success => "Completed",
        Severity.Warning => "Needs attention",
        Severity.Error => "Action failed",
        Severity.Info => "Update",
        _ => "Notification",
    };

    private static string IconFor(Severity severity) => severity switch
    {
        Severity.Success => Icons.Material.Filled.CheckCircle,
        Severity.Warning => Icons.Material.Filled.Warning,
        Severity.Error => Icons.Material.Filled.Error,
        _ => Icons.Material.Filled.Notifications,
    };

    private static string ToastSeverityClass(Severity severity) => severity switch
    {
        Severity.Success => "doxie-toast-success",
        Severity.Warning => "doxie-toast-warning",
        Severity.Error => "doxie-toast-error",
        _ => "doxie-toast-info",
    };

    private static string Compact(string? value, int maxChars)
    {
        var clean = string.Join(' ', (value ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length <= maxChars) return clean;
        return clean[..Math.Max(0, maxChars - 3)].TrimEnd() + "...";
    }

    private sealed record ToastText(string Title, string Body);
}
