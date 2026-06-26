using Microsoft.Data.SqlClient;
using System.Data;
using System.Text.Json;
using TokenRefresh;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var connectionString = app.Configuration.GetConnectionString("IntAcct")
    ?? throw new InvalidOperationException("ConnectionStrings:IntAcct is required");
var tokenUrl     = app.Configuration["IntAcct:TokenUrl"]     ?? "https://api.intacct.com/ia/api/v1/oauth2/token";
var queryUrl     = app.Configuration["IntAcct:QueryUrl"]     ?? "https://api.intacct.com/ia/api/v1/services/core/query";
var clientId     = app.Configuration["IntAcct:ClientId"]     ?? throw new InvalidOperationException("IntAcct:ClientId is required");
var clientSecret = app.Configuration["IntAcct:ClientSecret"] ?? throw new InvalidOperationException("IntAcct:ClientSecret is required");
var entityId     = app.Configuration["IntAcct:EntityId"]     ?? throw new InvalidOperationException("IntAcct:EntityId is required");
var objectsBaseUrl = app.Configuration["IntAcct:ObjectsBaseUrl"] ?? "https://api.intacct.com/ia/api/v1/objects";

using var http = new HttpClient();

async Task<string> GetLatestAccessTokenAsync()
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(
        "SELECT TOP 1 access_token FROM Production_Tokens ORDER BY datetime DESC", conn);
    return await cmd.ExecuteScalarAsync() as string
        ?? throw new InvalidOperationException("No access_token found in Production_Tokens");
}

async Task<string> RefreshTokenAsync()
{
    string refreshToken;
    await using (var conn = new SqlConnection(connectionString))
    {
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 refresh_token FROM Production_Tokens ORDER BY datetime DESC", conn);
        refreshToken = await cmd.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException("No refresh_token found");
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

    var tokens = JsonSerializer.Deserialize<OAuthTokenResponse>(await oauthResponse.Content.ReadAsStringAsync())
        ?? throw new InvalidOperationException("Failed to deserialize token response");

    await using (var conn = new SqlConnection(connectionString))
    {
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("IntAcct_InsertToken", conn)
        {
            CommandType = CommandType.StoredProcedure
        };
        cmd.Parameters.AddWithValue("@access_token",  tokens.AccessToken);
        cmd.Parameters.AddWithValue("@refresh_token", tokens.RefreshToken);
        await cmd.ExecuteNonQueryAsync();
    }

    return await GetLatestAccessTokenAsync();
}

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

async Task<HttpResponseMessage> SendCustomerDetailRequestAsync(string accessToken, string url)
{
    var request = new HttpRequestMessage(HttpMethod.Get, url);
    request.Headers.Add("Authorization", $"Bearer {accessToken}");
    request.Headers.Add("Cookie", "DFT_LOCALE=en_US.UTF-8");
    return await http.SendAsync(request);
}

// GET /api/customers — read all from Customers_RESTAPI
app.MapGet("/api/customers", async () =>
{
    var customers = new List<object>();
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    await using var cmd = new SqlCommand(
        "SELECT id, [key], href FROM [dbo].[Customers_RESTAPI] ORDER BY id", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        customers.Add(new
        {
            id   = reader.GetString(0),
            key  = reader.GetInt32(1),
            href = reader.GetString(2)
        });
    }
    return Results.Ok(new { count = customers.Count, customers });
});

// POST /api/refresh — sync from Intacct into Customers_RESTAPI
app.MapPost("/api/refresh", async () =>
{
    try
    {
        var accessToken = await GetLatestAccessTokenAsync();
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
                ?? throw new InvalidOperationException("Failed to deserialize customer page");

            customers.AddRange(page.Results);
            next = page.Meta.Next;
        }

        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
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
            }
        }

        return Results.Ok(new
        {
            fetched = customers.Count,
            message = $"Synced {customers.Count} customers from Intacct."
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// GET /api/customer/{id} — fetch name and address for a single customer from Intacct
app.MapGet("/api/customer/{id}", async (string id) =>
{
    try
    {
        int key;
        await using (var conn = new SqlConnection(connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT [key] FROM [dbo].[Customers_RESTAPI] WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            var result = await cmd.ExecuteScalarAsync();
            if (result is null)
                return Results.NotFound(new { detail = $"Customer '{id}' not found in database" });
            key = Convert.ToInt32(result);
        }

        var url = $"{objectsBaseUrl}/accounts-receivable/customer/{key}";
        var accessToken = await GetLatestAccessTokenAsync();
        var response = await SendCustomerDetailRequestAsync(accessToken, url);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            accessToken = await RefreshTokenAsync();
            response = await SendCustomerDetailRequestAsync(accessToken, url);
        }
        response.EnsureSuccessStatusCode();

        var wrapper = JsonSerializer.Deserialize<CustomerDetailWrapper>(
            await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Failed to deserialize customer detail");

        var detail = wrapper.Result
            ?? throw new InvalidOperationException("No ia::result in customer detail response");

        static string? NotEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        var addr = detail.Contacts?.Default?.MailingAddress;
        return Results.Ok(new
        {
            name = NotEmpty(detail.Name),
            address = addr == null ? null : new
            {
                line1    = NotEmpty(addr.Line1),
                line2    = NotEmpty(addr.Line2),
                city     = NotEmpty(addr.City),
                state    = NotEmpty(addr.State),
                postCode = NotEmpty(addr.PostCode),
                country  = NotEmpty(addr.Country),
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();
