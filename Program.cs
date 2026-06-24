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
var clientId     = config["IntAcct:ClientId"]     ?? throw new InvalidOperationException("IntAcct:ClientId is required");
var clientSecret = config["IntAcct:ClientSecret"] ?? throw new InvalidOperationException("IntAcct:ClientSecret is required");
var entityId     = config["IntAcct:EntityId"]     ?? throw new InvalidOperationException("IntAcct:EntityId is required");

// Step 1: Read latest refresh_token from DB
string currentRefreshToken;
await using (var conn = new SqlConnection(connectionString))
{
    await conn.OpenAsync();
    const string sql = """
        SELECT TOP 1 refresh_token
        FROM   [dbo].[Production_Tokens]
        ORDER  BY [datetime] DESC
        """;
    await using var cmd = new SqlCommand(sql, conn);
    var result = await cmd.ExecuteScalarAsync();
    currentRefreshToken = result as string
        ?? throw new InvalidOperationException("No refresh_token found in Production_Tokens");
}
Console.WriteLine("Read refresh_token from DB.");

// Step 2: POST to Intacct OAuth endpoint
using var http = new HttpClient();
var form = new Dictionary<string, string>
{
    ["grant_type"]    = "refresh_token",
    ["refresh_token"] = currentRefreshToken,
    ["client_id"]     = clientId,
    ["client_secret"] = clientSecret,
    ["entity_id"]     = entityId,
};
var response = await http.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
response.EnsureSuccessStatusCode();
var body = await response.Content.ReadAsStringAsync();
Console.WriteLine("Token refresh POST succeeded.");

// Step 3: Parse response
var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(body)
    ?? throw new InvalidOperationException("Failed to deserialize token response");
Console.WriteLine("Parsed new access_token and refresh_token.");

// Step 4: Persist via stored procedure
await using (var conn = new SqlConnection(connectionString))
{
    await conn.OpenAsync();
    await using var cmd = new SqlCommand("IntAcct_InsertToken", conn)
    {
        CommandType = CommandType.StoredProcedure
    };
    cmd.Parameters.AddWithValue("@access_token",  tokenResponse.AccessToken);
    cmd.Parameters.AddWithValue("@refresh_token", tokenResponse.RefreshToken);
    await cmd.ExecuteNonQueryAsync();
}
Console.WriteLine("Stored new tokens via IntAcct_InsertToken. Done.");
