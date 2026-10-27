namespace AIHelper.Core.Sparrow;

/// <summary>
/// Centralized builders for the Sparrow market-data endpoints. Keeps the exact URL shape
/// (query parameter order and the ?/&amp; refresh prefix) in one place so endpoint changes
/// only touch this file.
/// </summary>
public static class SparrowEndpoints
{
    public const string RefreshQuery = "refresh=1";

    /// <summary>Batch quote endpoint. <paramref name="codes"/> are comma-separated, already-normalized codes.</summary>
    public static string Quote(string codes, bool refresh) =>
        "/api/quote?code=" + codes + (refresh ? "&" + RefreshQuery : "");

    /// <summary>Full-market quote snapshot endpoint.</summary>
    public static string QuoteAll(bool refresh) =>
        "/api/quote-all" + (refresh ? "?" + RefreshQuery : "");

    /// <summary>Daily K-line endpoint. Pass <paramref name="type"/> = null to omit the type parameter (legacy V2 shape).</summary>
    public static string KlineAll(string code, int limit, bool refresh, string? type = "day")
    {
        string query = "/api/kline-all?code=" + code;
        if (!string.IsNullOrEmpty(type))
        {
            query += "&type=" + type;
        }
        query += "&limit=" + limit;
        if (refresh)
        {
            query += "&" + RefreshQuery;
        }
        return query;
    }

    /// <summary>Index daily endpoint.</summary>
    public static string Index(string code, int limit, bool refresh) =>
        "/api/index?code=" + code + "&limit=" + limit + (refresh ? "&" + RefreshQuery : "");

    /// <summary>Intraday trend endpoint.</summary>
    public static string Trend(string code, bool refresh) =>
        "/api/trend?code=" + code + (refresh ? "&" + RefreshQuery : "");

    /// <summary>Industry-board heat ranking endpoint (EastMoney clist, fs=m:90+t:2).</summary>
    public static string BoardHeat(bool refresh) =>
        "/api/board-heat" + (refresh ? "?" + RefreshQuery : "");

    /// <summary>Full-market symbol-to-industry-board mapping endpoint (EastMoney clist, f100).</summary>
    public static string IndustryMap(bool refresh) =>
        "/api/industry-map" + (refresh ? "?" + RefreshQuery : "");
}
