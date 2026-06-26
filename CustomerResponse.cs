using System.Text.Json.Serialization;

namespace TokenRefresh;

record CustomerListResponse(
    [property: JsonPropertyName("ia::result")] Customer[] Results,
    [property: JsonPropertyName("ia::meta")]   PageMeta   Meta
);

record Customer(
    [property: JsonPropertyName("id")]   string Id,
    [property: JsonPropertyName("key")]  string Key,
    [property: JsonPropertyName("href")] string Href
);

record PageMeta(
    [property: JsonPropertyName("next")] int? Next
);

record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken
);
