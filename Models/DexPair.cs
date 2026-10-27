using System.Text.Json.Serialization;
using DexScannerBot.Core;

namespace DexScannerBot.Models
{
    public class DexResponse
    {
        [JsonPropertyName("pairs")] public List<DexPair>? Pairs { get; set; }
    }

    public class DexPair
    {
        [JsonPropertyName("chainId")]       public string?    ChainId       { get; set; }
        [JsonPropertyName("dexId")]         public string?    DexId         { get; set; }
        [JsonPropertyName("url")]           public string?    Url           { get; set; }
        [JsonPropertyName("pairAddress")]   public string?    PairAddress   { get; set; }
        [JsonPropertyName("baseToken")]     public Token?     BaseToken     { get; set; }
        [JsonPropertyName("quoteToken")]    public Token?     QuoteToken    { get; set; }
        [JsonPropertyName("priceUsd")]      public string?    PriceUsd      { get; set; }
        [JsonPropertyName("priceNative")]   public string?    PriceNative   { get; set; }
        [JsonPropertyName("fdv")]           [JsonConverter(typeof(LenientDoubleConverter))] public double Fdv           { get; set; }
        [JsonPropertyName("marketCap")]     [JsonConverter(typeof(LenientDoubleConverter))] public double MarketCap     { get; set; }
        [JsonPropertyName("liquidity")]     public Liquidity? Liquidity     { get; set; }
        [JsonPropertyName("volume")]        public VolData?   Volume        { get; set; }
        [JsonPropertyName("priceChange")]   public ChangeData? PriceChange  { get; set; }
        [JsonPropertyName("txns")]          public TxnData?   Txns          { get; set; }
        [JsonPropertyName("pairCreatedAt")] [JsonConverter(typeof(LenientLongConverter))] public long PairCreatedAt { get; set; }
        [JsonPropertyName("info")]          public TokenInfo? Info          { get; set; }
        [JsonPropertyName("boosts")]        public BoostData? Boosts        { get; set; }
    }

    public class Token
    {
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("name")]    public string? Name    { get; set; }
        [JsonPropertyName("symbol")]  public string? Symbol  { get; set; }
    }

    public class Liquidity
    {
        [JsonPropertyName("usd")]   [JsonConverter(typeof(LenientDoubleConverter))] public double Usd   { get; set; }
        [JsonPropertyName("base")]  [JsonConverter(typeof(LenientDoubleConverter))] public double Base  { get; set; }
        [JsonPropertyName("quote")] [JsonConverter(typeof(LenientDoubleConverter))] public double Quote { get; set; }
    }

    public class VolData
    {
        [JsonPropertyName("m5")]  [JsonConverter(typeof(LenientDoubleConverter))] public double M5  { get; set; }
        [JsonPropertyName("h1")]  [JsonConverter(typeof(LenientDoubleConverter))] public double H1  { get; set; }
        [JsonPropertyName("h6")]  [JsonConverter(typeof(LenientDoubleConverter))] public double H6  { get; set; }
        [JsonPropertyName("h24")] [JsonConverter(typeof(LenientDoubleConverter))] public double H24 { get; set; }
    }

    public class ChangeData
    {
        [JsonPropertyName("m5")]  [JsonConverter(typeof(LenientDoubleConverter))] public double M5  { get; set; }
        [JsonPropertyName("h1")]  [JsonConverter(typeof(LenientDoubleConverter))] public double H1  { get; set; }
        [JsonPropertyName("h6")]  [JsonConverter(typeof(LenientDoubleConverter))] public double H6  { get; set; }
        [JsonPropertyName("h24")] [JsonConverter(typeof(LenientDoubleConverter))] public double H24 { get; set; }
    }

    public class TxnData
    {
        [JsonPropertyName("m5")]  public TxnCount? M5  { get; set; }
        [JsonPropertyName("h1")]  public TxnCount? H1  { get; set; }
        [JsonPropertyName("h6")]  public TxnCount? H6  { get; set; }
        [JsonPropertyName("h24")] public TxnCount? H24 { get; set; }
    }

    public class TxnCount
    {
        [JsonPropertyName("buys")]  [JsonConverter(typeof(LenientIntConverter))] public int Buys  { get; set; }
        [JsonPropertyName("sells")] [JsonConverter(typeof(LenientIntConverter))] public int Sells { get; set; }
        public int    Total    => Buys + Sells;
        public double BuyRatio => Total > 0 ? (double)Buys / Total : 0.5;
    }

    public class TokenInfo
    {
        [JsonPropertyName("websites")] public List<WebLink>?    Websites { get; set; }
        [JsonPropertyName("socials")]  public List<SocialLink>? Socials  { get; set; }
    }

    public class WebLink
    {
        [JsonPropertyName("url")]   public string? Url   { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
    }

    public class SocialLink
    {
        [JsonPropertyName("url")]  public string? Url  { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    public class BoostData
    {
        [JsonPropertyName("active")] [JsonConverter(typeof(LenientIntConverter))] public int Active { get; set; }
    }
}
