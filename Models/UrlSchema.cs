using System.Text.Json;

namespace QrCodeGenerator.Models;

/// <summary>Root of the url-schema.json file.</summary>
public sealed class UrlSchemaJson
{
    /// <summary>Base domain, e.g. "https://example.com".</summary>
    public string? Domain { get; set; }

    /// <summary>Optional path after the domain, e.g. "landing" or "campaign/spring".</summary>
    public string? Prefix { get; set; }

    /// <summary>Ordered list of query parameters shown as inputs.</summary>
    public List<UrlParamJson>? Parameters { get; set; }
}

/// <summary>A single query parameter definition.</summary>
public sealed class UrlParamJson
{
    /// <summary>Query key (e.g. "utm_source"). Required.</summary>
    public string? Name { get; set; }

    /// <summary>Friendly label for the input; falls back to Name when omitted.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// Pre-fill value(s). A single value (string/number/bool) renders as a textbox;
    /// a JSON array of values renders as a dropdown with the first entry pre-selected.
    /// </summary>
    public JsonElement? Default { get; set; }

    /// <summary>When true (the default), the parameter is left out of the URL if its value is empty.</summary>
    public bool? OmitIfEmpty { get; set; }

    /// <summary>Force this field to span the full width of the two-column layout.</summary>
    public bool? Wide { get; set; }

    /// <summary>Flattens <see cref="Default"/> to a list of strings (empty, one, or many).</summary>
    public List<string> ResolveDefaultValues()
    {
        var values = new List<string>();
        if (Default is not JsonElement el) return values;

        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in el.EnumerateArray())
                values.Add(ScalarToString(item));
        }
        else
        {
            values.Add(ScalarToString(el));
        }
        return values;
    }

    private static string ScalarToString(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Number => e.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => string.Empty,
        _ => e.GetRawText(),
    };
}

/// <summary>Binds a parameter definition to a UI control's current value.</summary>
internal sealed class ParamInput
{
    public UrlParamJson Def { get; }
    public Func<string> GetValue { get; }

    public ParamInput(UrlParamJson def, Func<string> getValue)
    {
        Def = def;
        GetValue = getValue;
    }
}
