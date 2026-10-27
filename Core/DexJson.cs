using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DexScannerBot.Models;

namespace DexScannerBot.Core
{
    /// <summary>
    /// Shared JSON helpers for DexScreener payloads.
    ///
    /// DexScreener occasionally sends <c>null</c> (or a numeric string) for numeric fields
    /// such as <c>marketCap</c>, <c>fdv</c> or <c>liquidity.usd</c>. System.Text.Json throws
    /// on that by default, which used to discard an entire 30-token batch. The lenient
    /// converters below read null as 0 and accept numbers sent as strings.
    /// </summary>
    public static class DexJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        /// <summary>
        /// Parses a DexScreener pair list. Handles both response shapes:
        ///   /tokens/v1/{chain}/{addrs}  → JSON array of pairs
        ///   /latest/dex/tokens/{addrs}  → { "pairs": [...] }
        /// Returns an empty list on any malformed input.
        /// </summary>
        public static List<DexPair> ParsePairs(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return new List<DexPair>();
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                JsonElement arr;
                if (root.ValueKind == JsonValueKind.Array) arr = root;
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("pairs", out var p) && p.ValueKind == JsonValueKind.Array) arr = p;
                else return new List<DexPair>();

                var list = new List<DexPair>(arr.GetArrayLength());
                foreach (var el in arr.EnumerateArray())
                {
                    try
                    {
                        var pair = el.Deserialize<DexPair>(Options);
                        if (pair != null) list.Add(pair);
                    }
                    catch { /* one malformed pair must not poison the batch */ }
                }
                return list;
            }
            catch
            {
                return new List<DexPair>();
            }
        }

        /// <summary>Highest-liquidity pair per base-token address (case-insensitive).</summary>
        public static Dictionary<string, DexPair> BestPairPerToken(IEnumerable<DexPair> pairs)
        {
            return pairs.Where(p => !string.IsNullOrEmpty(p.BaseToken?.Address))
                .GroupBy(p => p.BaseToken!.Address!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Liquidity?.Usd ?? 0).First(), StringComparer.OrdinalIgnoreCase);
        }

        public static double ParsePrice(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Replace("$", "").Trim();
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && !double.IsInfinity(v) ? v : 0;
        }
    }

    /// <summary>Reads null → 0, numbers, and numeric strings into a double.</summary>
    public sealed class LenientDoubleConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.TryGetDouble(out var d) ? d : 0;
                case JsonTokenType.String:
                    return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
                case JsonTokenType.Null:
                    return 0;
                default:
                    reader.Skip();
                    return 0;
            }
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }

    /// <summary>Reads null → 0, numbers, and numeric strings into a long.</summary>
    public sealed class LenientLongConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var l)) return l;
                    return reader.TryGetDouble(out var d) ? (long)d : 0;
                case JsonTokenType.String:
                    return long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
                case JsonTokenType.Null:
                    return 0;
                default:
                    reader.Skip();
                    return 0;
            }
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }

    /// <summary>Reads null → 0, numbers, and numeric strings into an int.</summary>
    public sealed class LenientIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    if (reader.TryGetInt32(out var i)) return i;
                    return reader.TryGetDouble(out var d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : 0;
                case JsonTokenType.String:
                    return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
                case JsonTokenType.Null:
                    return 0;
                default:
                    reader.Skip();
                    return 0;
            }
        }

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
            => writer.WriteNumberValue(value);
    }
}
