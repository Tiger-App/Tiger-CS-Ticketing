using Microsoft.AspNetCore.Html;

namespace TigerCS.Web.Models;

/// <summary>
/// The small inline line icons Ticket Details uses (24px grid, 1.7 stroke, <c>currentColor</c>). Inline SVG keeps them inside the page's
/// Content-Security-Policy (no fetched assets), lets them follow the text colour, and they are always decorative:
/// <c>aria-hidden</c> — the label next to each one carries the meaning.
/// </summary>
public static class TicketIcons
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["phone"] = "<path d=\"M5 4h3.2l1.6 4-2 1.3a11 11 0 0 0 6.9 6.9l1.3-2 4 1.6V19a2 2 0 0 1-2 2A16 16 0 0 1 3 6a2 2 0 0 1 2-2Z\"/>",
        ["chat"] = "<path d=\"M4 5.5A2.5 2.5 0 0 1 6.5 3h11A2.5 2.5 0 0 1 20 5.5v8a2.5 2.5 0 0 1-2.5 2.5H11l-4.5 4v-4H6.5A2.5 2.5 0 0 1 4 13.5Z\"/>",
        ["mail"] = "<rect x=\"3\" y=\"5\" width=\"18\" height=\"14\" rx=\"2.5\"/><path d=\"m3.5 7 8.5 6 8.5-6\"/>",
        ["ticket"] = "<path d=\"M4 8.5V6.5A1.5 1.5 0 0 1 5.5 5h13A1.5 1.5 0 0 1 20 6.5v2a2.5 2.5 0 0 0 0 5v2a1.5 1.5 0 0 1-1.5 1.5h-13A1.5 1.5 0 0 1 4 15.5v-2a2.5 2.5 0 0 0 0-5Z\"/><path d=\"M14 5v14\" stroke-dasharray=\"2 2.4\"/>",
        ["user"] = "<circle cx=\"12\" cy=\"8\" r=\"3.6\"/><path d=\"M4.5 20a7.5 7.5 0 0 1 15 0\"/>",
        ["users"] = "<circle cx=\"9\" cy=\"8.5\" r=\"3.2\"/><path d=\"M2.8 19.5a6.2 6.2 0 0 1 12.4 0\"/><path d=\"M15.5 5.6a3.2 3.2 0 0 1 0 5.8M17.4 14.4a6.2 6.2 0 0 1 3.8 5.1\"/>",
        ["building"] = "<path d=\"M5 21V4.5A1.5 1.5 0 0 1 6.5 3h7A1.5 1.5 0 0 1 15 4.5V21M15 9h2.5A1.5 1.5 0 0 1 19 10.5V21M3 21h18\"/><path d=\"M8.5 7h3M8.5 10.5h3M8.5 14h3M17 13h.01M17 16.5h.01\"/>",
        ["target"] = "<circle cx=\"11\" cy=\"13\" r=\"7\"/><circle cx=\"11\" cy=\"13\" r=\"3\"/><path d=\"m11 13 8-8M16.5 4.5V8H20\"/>",
        ["doc"] = "<path d=\"M6.5 3h7.8L19 7.7V19.5A1.5 1.5 0 0 1 17.5 21h-11A1.5 1.5 0 0 1 5 19.5v-15A1.5 1.5 0 0 1 6.5 3Z\"/><path d=\"M14 3.3V8h4.7M8.5 12.5h7M8.5 16h7M8.5 9h2.5\"/>",
        ["clock"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 7v5l3.2 2\"/>",
        ["star"] = "<path d=\"m12 3.5 2.6 5.3 5.9.9-4.3 4.1 1 5.8-5.2-2.8-5.2 2.8 1-5.8-4.3-4.1 5.9-.9Z\"/>",
        ["list"] = "<path d=\"M9.5 6.5H20M9.5 12H20M9.5 17.5H20\"/><path d=\"M4.5 6.5h.01M4.5 12h.01M4.5 17.5h.01\" stroke-width=\"2.6\"/>",
        ["check-circle"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"m8 12.3 2.8 2.8L16.2 9.5\"/>",
        ["calendar"] = "<rect x=\"3.5\" y=\"5\" width=\"17\" height=\"15.5\" rx=\"2.5\"/><path d=\"M3.5 10h17M8 3v4M16 3v4\"/>",
        ["chevron-down"] = "<path d=\"m6 9.5 6 6 6-6\"/>",
        ["chevron-right"] = "<path d=\"m9.5 6 6 6-6 6\"/>",
        ["external"] = "<path d=\"M13.5 4H20v6.5M20 4l-8.5 8.5M18 14v4.5a1.5 1.5 0 0 1-1.5 1.5h-11A1.5 1.5 0 0 1 4 18.5v-11A1.5 1.5 0 0 1 5.5 6H10\"/>",
        ["info"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 11v5.5M12 7.6h.01\" stroke-width=\"2\"/>",
        ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m21 21-4.35-4.35\"/>",
        ["close"] = "<path d=\"m6 6 12 12M18 6 6 18\"/>",
    };

    /// <summary>One icon as an inline <c>&lt;svg&gt;</c>; <paramref name="css"/> is its class (default <c>ic</c>).</summary>
    public static IHtmlContent Svg(string name, string css = "ic")
    {
        var body = Paths.TryGetValue(name, out var found) ? found : Paths["ticket"];
        return new HtmlString($"<svg class=\"{css} ic-{name}\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">{body}</svg>");
    }

    /// <summary>The icon for the channel a ticket came in on — a phone for calls, a bubble for chats, an envelope for e-mail, a ticket otherwise.</summary>
    public static string ForChannel(string? channelName)
    {
        var name = channelName ?? "";
        bool Has(string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
        return Has("phone") || Has("call") || Has("voice") ? "phone"
            : Has("mail") ? "mail"
            : Has("chat") || Has("web") || Has("whatsapp") || Has("sms") || Has("message") ? "chat"
            : "ticket";
    }
}
