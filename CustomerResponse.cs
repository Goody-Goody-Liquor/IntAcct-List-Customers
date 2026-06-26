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

// Wrapper: the single-object response is inside "ia::result"
record CustomerDetailWrapper(
    [property: JsonPropertyName("ia::result")] CustomerDetailResult? Result
);

record CustomerDetailResult(
    [property: JsonPropertyName("name")]     string?          Name,
    [property: JsonPropertyName("contacts")] CustomerContacts? Contacts
);

// contacts.default.mailingAddress
record CustomerContacts(
    [property: JsonPropertyName("default")] CustomerContactDefault? Default
);

record CustomerContactDefault(
    [property: JsonPropertyName("mailingAddress")] MailingAddress? MailingAddress
);

record MailingAddress(
    [property: JsonPropertyName("addressLine1")] string? Line1,
    [property: JsonPropertyName("addressLine2")] string? Line2,
    [property: JsonPropertyName("city")]         string? City,
    [property: JsonPropertyName("state")]        string? State,
    [property: JsonPropertyName("postCode")]     string? PostCode,
    [property: JsonPropertyName("country")]      string? Country
);
