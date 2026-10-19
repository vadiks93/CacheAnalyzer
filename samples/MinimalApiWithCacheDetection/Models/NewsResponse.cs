using System.Text.Json;
using System.Text.Json.Serialization;

namespace MinimalApiWithCacheDetection.Models;

// Explicit names work with GetFromJsonAsync's default JSON options.
public sealed class NewsResponse
{
    public string? Status { get; init; }
    public int Limit { get; init; }
    public string? Path { get; init; }
    public int Page { get; init; }
    [JsonPropertyName("has_next_pages")] public bool HasNextPages { get; init; }
    [JsonPropertyName("next_page")] public string? NextPage { get; init; }
    [JsonPropertyName("has_previous_page")] public bool HasPreviousPage { get; init; }
    [JsonPropertyName("previous_page")] public string? PreviousPage { get; init; }
    public Dictionary<string, string> Export { get; init; } = [];
    [JsonPropertyName("request_id")] public string? RequestId { get; init; }
    public List<NewsArticle> Results { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed class NewsArticle
{
    public long Id { get; init; }
    public string? Href { get; init; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Body { get; init; }
    [JsonPropertyName("body_html")] public string? BodyHtml { get; init; }
    public string? Language { get; init; }
    public Dictionary<string, NewsTranslation> Translations { get; init; } = [];
    public NewsAuthor? Author { get; init; }
    public string? Image { get; init; }
    public List<NewsClassification> Categories { get; init; } = [];
    public List<NewsClassification> Topics { get; init; } = [];
    public List<NewsClassification> Industries { get; init; } = [];
    public List<NewsEntity> Entities { get; init; } = [];
    [JsonPropertyName("locations_mentioned")] public List<NewsLocation> LocationsMentioned { get; init; } = [];
    public NewsSource? Source { get; init; }
    public NewsArticleSentiment? Sentiment { get; init; }
    public List<NewsSummary> Summary { get; init; } = [];
    public List<string> Keywords { get; init; } = [];
    public List<NewsMedia> Links { get; init; } = [];
    public List<NewsMedia> Media { get; init; } = [];
    public NewsReadability? Readability { get; init; }
    public NewsStory? Story { get; init; }
    public Dictionary<string, long> Shares { get; init; } = [];
    [JsonPropertyName("is_duplicate")] public bool IsDuplicate { get; init; }
    [JsonPropertyName("is_free")] public bool IsFree { get; init; }
    [JsonPropertyName("is_breaking")] public bool IsBreaking { get; init; }
    [JsonPropertyName("read_time")] public int ReadTime { get; init; }
    [JsonPropertyName("sentences_count")] public int SentencesCount { get; init; }
    [JsonPropertyName("paragraphs_count")] public int ParagraphsCount { get; init; }
    [JsonPropertyName("words_count")] public int WordsCount { get; init; }
    [JsonPropertyName("characters_count")] public int CharactersCount { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record NewsTranslation(string? Title, string? Description);
public sealed record NewsAuthor(long? Id, string? Name);

public sealed class NewsClassification
{
    // Category/industry IDs are numbers; topic IDs are strings.
    public JsonElement Id { get; init; }
    public string? Name { get; init; }
    public double? Score { get; init; }
    public string? Taxonomy { get; init; }
    public Dictionary<string, string> Links { get; init; } = [];
}

public sealed class NewsEntity
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public Dictionary<string, string> Links { get; init; } = [];
    public int Frequency { get; init; }
    public NewsSentiment? Sentiment { get; init; }
    public NewsPositions? Title { get; init; }
    public NewsPositions? Body { get; init; }
    // Shape depends on entity type. Dates can contain 00 components and
    // social_profiles can be either an object or an empty array.
    public JsonElement Metadata { get; init; }
}

public sealed record NewsPositions(List<NewsPosition> Pos);
public sealed record NewsPosition(int Start, int End);
public sealed record NewsLocation(string? Name, string? Country, double? Lat, double? Lng, string? Type);
public sealed record NewsSentiment(double Score, string? Polarity, NewsMentions? Mentions);
public sealed record NewsMentions(int Positive, int Neutral, int Negative);
public sealed record NewsArticleSentiment(NewsSentiment? Overall, NewsSentiment? Title, NewsSentiment? Body);
public sealed record NewsSummary(string? Sentence, NewsSentiment? Sentiment);
public sealed record NewsMedia(string? Url, string? Type);
public sealed record NewsStory(long Id, string? Uri);

public sealed class NewsSource
{
    public long Id { get; init; }
    public string? Domain { get; init; }
    [JsonPropertyName("home_page_url")] public string? HomePageUrl { get; init; }
    public string? Type { get; init; }
    public string? Bias { get; init; }
    public Dictionary<string, double> Rankings { get; init; } = [];
    public NewsSourceLocation? Location { get; init; }
    public string? Favicon { get; init; }
}

public sealed class NewsSourceLocation
{
    [JsonPropertyName("country_name")] public string? CountryName { get; init; }
    [JsonPropertyName("country_code")] public string? CountryCode { get; init; }
}

public sealed class NewsReadability
{
    [JsonPropertyName("flesch_kincaid_grade")] public double? FleschKincaidGrade { get; init; }
    [JsonPropertyName("flesch_reading_ease")] public double? FleschReadingEase { get; init; }
    [JsonPropertyName("automated_readability_index")] public double? AutomatedReadabilityIndex { get; init; }
    [JsonPropertyName("difficulty_level")] public string? DifficultyLevel { get; init; }
    [JsonPropertyName("target_audience")] public string? TargetAudience { get; init; }
    [JsonPropertyName("reading_age")] public double? ReadingAge { get; init; }
    [JsonPropertyName("avg_words_per_sentence")] public double? AvgWordsPerSentence { get; init; }
    [JsonPropertyName("avg_syllables_per_word")] public double? AvgSyllablesPerWord { get; init; }
}
