using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using System.Data;
using System.Text.Json;
using TokenRefresh;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .Build();

var connectionString = config.GetConnectionString("IntAcct")
    ?? throw new InvalidOperationException("ConnectionStrings:IntAcct is required");
var tokenUrl     = config["IntAcct:TokenUrl"]     ?? "https://api.intacct.com/ia/api/v1/oauth2/token";
var queryUrl     = config["IntAcct:QueryUrl"]     ?? "https://api.intacct.com/ia/api/v1/services/core/query";
var clientId     = config["IntAcct:ClientId"]     ?? throw new InvalidOperationException("IntAcct:ClientId is required");
var clientSecret = config["IntAcct:ClientSecret"] ?? throw new InvalidOperationException("IntAcct:ClientSecret is required");
var entityId     = config["IntAcct:EntityId"]     ?? throw new InvalidOperationException("IntAcct:EntityId is required");

using var http = new HttpClient();

// Get the latest access_token from Production_Tokens
async Task<string> GetLatestAccessTokenAsync()
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(
        "SELECT TOP 1 access_token FROM Production_Tokens ORDER BY datetime DESC", conn);
    var result = await cmd.ExecuteScalarAsync();
    return result as string ?? throw new InvalidOperationException("No access_token found in Production_Tokens");
}

// Refresh token via OAuth, store via stored procedure, return new access_token
async Task<string> RefreshTokenAsync()
{
    Console.WriteLine("Token expired — refreshing...");

    string refreshToken;
    await using (var conn = new SqlConnection(connectionString))
    {
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 refresh_token FROM Production_Tokens ORDER BY datetime DESC", conn);
        var result = await cmd.ExecuteScalarAsync();
        refreshToken = result as string ?? throw new InvalidOperationException("No refresh_token found in Production_Tokens");
    }

    var form = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"]    = "refresh_token",
        ["refresh_token"] = refreshToken,
        ["client_id"]     = clientId,
        ["client_secret"] = clientSecret,
        ["entity_id"]     = entityId,
    });
    var oauthResponse = await http.PostAsync(tokenUrl, form);
    oauthResponse.EnsureSuccessStatusCode();

    var oauthTokens = JsonSerializer.Deserialize<OAuthTokenResponse>(await oauthResponse.Content.ReadAsStringAsync())
        ?? throw new InvalidOperationException("Failed to deserialize OAuth token response");

    await using (var conn = new SqlConnection(connectionString))
    {
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("IntAcct_InsertToken", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@access_token",  oauthTokens.AccessToken);
        cmd.Parameters.AddWithValue("@refresh_token", oauthTokens.RefreshToken);
        await cmd.ExecuteNonQueryAsync();
    }

    Console.WriteLine("Token refreshed and stored.");
    return await GetLatestAccessTokenAsync();
}

// Build and send a customer query request
async Task<HttpResponseMessage> SendCustomerRequestAsync(string accessToken, int start)
{
    var body = JsonSerializer.Serialize(new
    {
        @object = "accounts-receivable/customer",
        fields  = new[] { "id", "key", "href" },
        start
    });

    var request = new HttpRequestMessage(HttpMethod.Post, queryUrl)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    };
    request.Headers.Add("Authorization", $"Bearer {accessToken}");
    request.Headers.Add("Cookie", "DFT_LOCALE=en_US.UTF-8");

    return await http.SendAsync(request);
}

// Step 1: Get access token
var accessToken = await GetLatestAccessTokenAsync();
Console.WriteLine("Retrieved access_token from DB.");

// Step 2: Page through all customers, refreshing token on 401
var customers = new List<Customer>();
int? next = 1;

while (next.HasValue)
{
    var response = await SendCustomerRequestAsync(accessToken, next.Value);

    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
    {
        accessToken = await RefreshTokenAsync();
        response = await SendCustomerRequestAsync(accessToken, next.Value);
    }

    response.EnsureSuccessStatusCode();

    var page = JsonSerializer.Deserialize<CustomerListResponse>(await response.Content.ReadAsStringAsync())
        ?? throw new InvalidOperationException("Failed to deserialize customer response");

    customers.AddRange(page.Results);
    next = page.Meta.Next;
    Console.WriteLine($"Fetched {page.Results.Length} customers (total so far: {customers.Count})");
}

// Step 3: Insert all customers via stored procedure
Console.WriteLine($"\nInserting {customers.Count} customers into SQL...");
await using (var conn = new SqlConnection(connectionString))
{
    await conn.OpenAsync();
    int inserted = 0;
    foreach (var c in customers)
    {
        await using var cmd = new SqlCommand("IntAcct_InsertCustomer_RESTAPI", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@id",   c.Id);
        cmd.Parameters.AddWithValue("@key",  int.Parse(c.Key));
        cmd.Parameters.AddWithValue("@href", c.Href);
        await cmd.ExecuteNonQueryAsync();
        inserted++;
    }
    Console.WriteLine($"Done — {inserted} customers processed.");
}

// Step 4: Print results
Console.WriteLine($"\n{"id",-15} {"key",-6} href");
Console.WriteLine(new string('-', 70));
foreach (var c in customers)
    Console.WriteLine($"{c.Id,-15} {c.Key,-6} {c.Href}");

Console.WriteLine($"\nTotal customers: {customers.Count}");
