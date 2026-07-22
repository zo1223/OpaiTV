using System.Text;
using System.Text.Json;

namespace OrionTV.Desktop.Core;

public static class VideoSourceSubscriptionParser
{
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private static readonly Dictionary<char, int> Base58Map = Base58Alphabet
        .Select((character, index) => new { character, index })
        .ToDictionary(item => item.character, item => item.index);

    public static VideoSourceSubscriptionConfig Parse(string payload)
    {
        var trimmed = payload.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("EMPTY_VIDEO_SOURCE_SUBSCRIPTION");
        }

        using var document = ParseDocument(trimmed);
        return Normalize(document.RootElement);
    }

    public static async Task<VideoSourceSubscriptionConfig> FetchAsync(HttpClient httpClient, string subscriptionUrl, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(subscriptionUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"VIDEO_SOURCE_SUBSCRIPTION_FETCH_FAILED:{(int)response.StatusCode}");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return Parse(payload);
    }

    private static JsonDocument ParseDocument(string payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(DecodeBase58(payload));
        }
    }

    private static VideoSourceSubscriptionConfig Normalize(JsonElement root)
    {
        if (!root.TryGetProperty("api_site", out var apiSite))
        {
            throw new InvalidOperationException("INVALID_VIDEO_SOURCE_SUBSCRIPTION");
        }

        var sources = apiSite.ValueKind switch
        {
            JsonValueKind.Object => apiSite.EnumerateObject()
                .Select(property => MapSource(property.Name, property.Value))
                .Where(source => source is not null)
                .Cast<VideoSource>()
                .ToList(),
            JsonValueKind.Array => apiSite.EnumerateArray()
                .Select((item, index) => MapSource(GetString(item, "key") ?? GetString(item, "name") ?? $"source_{index}", item))
                .Where(source => source is not null)
                .Cast<VideoSource>()
                .ToList(),
            _ => []
        };

        if (sources.Count == 0)
        {
            throw new InvalidOperationException("EMPTY_VIDEO_SOURCE_SUBSCRIPTION");
        }

        return new VideoSourceSubscriptionConfig(sources);
    }

    private static VideoSource? MapSource(string key, JsonElement item)
    {
        var api = StripTrailingSlash(GetString(item, "api")?.Trim() ?? "");
        var name = GetString(item, "name")?.Trim() ?? key;
        if (string.IsNullOrWhiteSpace(api) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var detail = GetString(item, "detail");
        var protocol = VideoSourceProtocols.Normalize(
            GetString(item, "protocol") ?? GetString(item, "type") ?? GetString(item, "format"));
        var dialect = GetString(item, "dialect");
        var options = ReadOptions(item);

        return new VideoSource(
            key,
            name,
            api,
            string.IsNullOrWhiteSpace(detail) ? null : StripTrailingSlash(detail.Trim()),
            protocol,
            string.IsNullOrWhiteSpace(dialect) ? null : dialect.Trim(),
            options);
    }

    private static IReadOnlyDictionary<string, string>? ReadOptions(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("options", out var options)
            || options.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in options.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(value))
            {
                map[property.Name] = value.Trim();
            }
        }

        return map.Count == 0 ? null : map;
    }

    private static string? GetString(JsonElement item, string propertyName)
    {
        return item.ValueKind == JsonValueKind.Object &&
               item.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string StripTrailingSlash(string value) => value.TrimEnd('/');

    private static string DecodeBase58(string value)
    {
        var bytes = new List<int> { 0 };
        foreach (var character in value.Trim())
        {
            if (!Base58Map.TryGetValue(character, out var digit))
            {
                throw new InvalidOperationException("INVALID_BASE58_SUBSCRIPTION");
            }

            var carry = digit;
            for (var i = 0; i < bytes.Count; i++)
            {
                var number = bytes[i] * 58 + carry;
                bytes[i] = number & 0xff;
                carry = number >> 8;
            }

            while (carry > 0)
            {
                bytes.Add(carry & 0xff);
                carry >>= 8;
            }
        }

        foreach (var character in value.Trim())
        {
            if (character == Base58Alphabet[0])
            {
                bytes.Add(0);
            }
            else
            {
                break;
            }
        }

        bytes.Reverse();
        return Encoding.UTF8.GetString(bytes.Select(item => (byte)item).ToArray());
    }
}
