using System.Net;
using Microsoft.AspNetCore.Html;

namespace TigerCS.Web.Services.EmployeeReviews;

/// <summary>
/// Every word the customer tablet shows, in English and Arabic, in one place. The page renders the chosen language into the HTML and
/// also carries the other language in <c>data-</c> attributes, so the Arabic/English toggle switches instantly with no network call
/// (the tablet may be offline) and the server-rendered page is correct before any script runs.
/// </summary>
public static class TabletText
{
    public const string English = "en";
    public const string Arabic = "ar";

    public static readonly IReadOnlyDictionary<string, (string En, string Ar)> Texts = new Dictionary<string, (string, string)>
    {
        ["served-by"] = ("You are being served by", "يخدمك اليوم"),
        ["question"] = ("How satisfied are you with our employee's service?", "ما مدى رضاك عن خدمة موظفنا؟"),
        ["star-1"] = ("Poor", "ضعيف"),
        ["star-2"] = ("Fair", "مقبول"),
        ["star-3"] = ("Good", "جيد"),
        ["star-4"] = ("Very good", "جيد جداً"),
        ["star-5"] = ("Excellent", "ممتاز"),
        ["rating-hint"] = ("Tap a star to rate", "اضغط على نجمة للتقييم"),
        ["comment-label"] = ("Comments (optional)", "ملاحظات (اختياري)"),
        ["comment-placeholder"] = ("Tell us more about your experience…", "أخبرنا المزيد عن تجربتك…"),
        ["submit"] = ("Submit", "إرسال"),
        ["sending"] = ("Sending…", "جارٍ الإرسال…"),
        ["choose-rating"] = ("Please choose a star rating first.", "يرجى اختيار عدد النجوم أولاً."),
        ["thanks-title"] = ("Thank you for your feedback!", "شكراً لملاحظاتك!"),
        ["thanks-sub"] = ("Your review has been recorded.", "تم تسجيل تقييمك."),
        ["pending-note"] = ("Your review is saved on this tablet and will be sent automatically.", "تم حفظ تقييمك على هذا الجهاز وسيُرسل تلقائياً."),
        ["inactive-title"] = ("This tablet is not active", "هذا الجهاز غير مفعّل"),
        ["inactive-sub"] = ("Please ask a member of staff to contact the administrator.", "يرجى مراجعة أحد الموظفين للتواصل مع المسؤول."),
        ["unavailable-title"] = ("Reviews are unavailable right now", "التقييم غير متاح حالياً"),
        ["unavailable-sub"] = ("Trying again automatically…", "جارٍ إعادة المحاولة تلقائياً…"),
        ["language-toggle"] = ("العربية", "English"),
        ["activate-title"] = ("Activate this tablet", "تفعيل هذا الجهاز"),
        ["activate-sub"] = ("Enter the activation code given by your administrator.", "أدخل رمز التفعيل الذي زوّدك به المسؤول."),
        ["activate-label"] = ("Activation code", "رمز التفعيل"),
        ["activate-button"] = ("Activate", "تفعيل"),
        ["activate-invalid"] = ("This code is not valid. Check it and try again.", "الرمز غير صالح. تحقق منه وحاول مجدداً."),
        ["activate-inactive"] = ("This tablet has been switched off by the administrator.", "تم إيقاف هذا الجهاز من قبل المسؤول."),
        ["activate-unavailable"] = ("The service could not be reached. Please try again.", "تعذّر الوصول إلى الخدمة. يرجى المحاولة مجدداً.")
    };

    /// <summary>The supported language for a requested code; anything unknown or missing is English.</summary>
    public static string Normalize(string? language) =>
        string.Equals(language?.Trim(), Arabic, StringComparison.OrdinalIgnoreCase) ? Arabic : English;

    public static string Get(string key, string language) =>
        Texts.TryGetValue(key, out var text) ? (Normalize(language) == Arabic ? text.Ar : text.En) : key;

    public static string Direction(string language) => Normalize(language) == Arabic ? "rtl" : "ltr";

    /// <summary>The attributes that let the toggle re-translate an element in the browser: its key and both texts, HTML-encoded.</summary>
    public static HtmlString Attrs(string key) => new(
        $"data-i18n=\"{WebUtility.HtmlEncode(key)}\" data-en=\"{WebUtility.HtmlEncode(Get(key, English))}\" data-ar=\"{WebUtility.HtmlEncode(Get(key, Arabic))}\"");
}
