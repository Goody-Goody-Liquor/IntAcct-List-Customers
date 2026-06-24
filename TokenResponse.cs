using System.Text.Json.Serialization;

namespace TokenRefresh;

record TokenResponse(
    [property: JsonPropertyName("access_token")]  string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken
);
