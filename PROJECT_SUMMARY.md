# IntAcct Customer Sync

A .NET 8 ASP.NET Core web application that connects Sage Intacct's REST API to a SQL Server database. It lets staff browse the full customer list, view each customer's name and mailing address pulled live from Intacct, and run a bulk sync that writes all Intacct customers into the local `Customers_RESTAPI` table.

The app also manages the OAuth2 token lifecycle — storing access and refresh tokens in SQL and automatically exchanging an expired token mid-operation without interrupting the user.

---

## Architecture

### Source Files

| File | Description |
|------|-------------|
| `Program.cs` | ASP.NET Core minimal API — all HTTP endpoints and business logic |
| `CustomerResponse.cs` | C# record types for deserializing Intacct REST responses |
| `wwwroot/index.html` | Single-page UI — vanilla HTML/CSS/JS, served as a static file |
| `TokenRefresh.csproj` | Project file — targets `Microsoft.NET.Sdk.Web`, net8.0 |
| `Dockerfile` | Two-stage build: SDK image compiles, ASP.NET runtime image runs |
| `appsettings.json` | Local credentials & config — never committed (in `.gitignore`) |
| `PROJECT_SUMMARY.md` | This document — architecture, flows, and run instructions |

### SQL Server Objects (IntAcct database on GOODY2)

| Object | Type | Description |
|--------|------|-------------|
| `dbo.Production_Tokens` | Table | Stores timestamped access/refresh token pairs |
| `dbo.Customers_RESTAPI` | Table | Local cache of every Intacct customer (id, key, href) |
| `IntAcct_InsertToken` | Stored Proc | Writes a new token pair with current timestamp |
| `IntAcct_InsertCustomer_RESTAPI` | Stored Proc | Upserts a customer record by id — safe to re-run |

---

## How It Works

### Page Load
```
Browser opens :8080  →  GET /api/customers  →  SELECT from Customers_RESTAPI  →  Populates dropdown + count
```

### Selecting a Customer
```
Click ID in list  →  GET /api/customer/{id}  →  Lookup key in Customers_RESTAPI
                                             →  Get latest access_token from Production_Tokens
                                             →  GET Intacct /objects/accounts-receivable/customer/{key}
                                             →  Display name + mailing address
```

### Refresh from Intacct Button
```
POST /api/refresh  →  Get latest access_token
                   →  POST Intacct /services/core/query  (paginate via ia::meta.next)
                   →  Call IntAcct_InsertCustomer_RESTAPI for each record
```

### Refresh Token Button
```
POST /api/token/refresh  →  Get latest refresh_token from Production_Tokens
                         →  POST Intacct OAuth2 /token
                         →  Call IntAcct_InsertToken with new access/refresh pair
```

### Automatic Token Renewal
Any Intacct API call that receives a `401 Unauthorized` response automatically triggers `RefreshTokenAsync()`, stores the new tokens, and retries the original request — no manual intervention required.

---

## API Endpoints

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/` | Serves the web UI (`wwwroot/index.html`) |
| `GET` | `/api/customers` | Returns all customers from `Customers_RESTAPI` — used to populate the dropdown on load |
| `GET` | `/api/customer/{id}` | Looks up the Intacct `key` for the given customer `id`, fetches name and mailing address live from Intacct |
| `POST` | `/api/refresh` | Pages through all customers in Intacct and upserts each into `Customers_RESTAPI` |
| `POST` | `/api/token/refresh` | Exchanges the latest refresh token for a new access/refresh pair and writes it to `Production_Tokens` |

---

## Running Locally

Create `appsettings.json` in the project root (this file is in `.gitignore` — never commit it):

```json
{
  "ConnectionStrings": {
    "IntAcct": "Server=GOODY2;Database=IntAcct;Integrated Security=True;TrustServerCertificate=True;"
  },
  "IntAcct": {
    "ClientId":     "<your-client-id>",
    "ClientSecret": "<your-client-secret>",
    "EntityId":     "GOODY"
  }
}
```

Then run:

```bash
dotnet run
```

Open `http://localhost:5000` (or the port shown in the terminal).

---

## Docker

### 0 — Start Docker Desktop (if not already running)

Docker Desktop must be running before any `docker` commands will work. Launch it from the Start menu or:

```powershell
Start-Process "C:\Program Files\Docker\Docker\Docker Desktop.exe"
```

Wait for the whale icon in the system tray to show **Engine running** before proceeding.

### 1 — Build the image

```bash
docker build -t intacct-token-refresh .
```

### 2 — Run the container

```bash
docker run --rm -d \
  --name intacct-web \
  --add-host=GOODY2:10.5.5.25 \
  -p 8080:8080 \
  -e "ConnectionStrings__IntAcct=Server=GOODY2;Database=IntAcct;User Id=<sql-user>;Password=<sql-password>;TrustServerCertificate=True;" \
  -e "IntAcct__ClientId=<client-id>" \
  -e "IntAcct__ClientSecret=<client-secret>" \
  -e "IntAcct__EntityId=GOODY" \
  intacct-token-refresh
```

> **Why `--add-host` and SQL auth?**
> Linux containers cannot use Windows authentication or resolve internal hostnames via DNS. The `--add-host` flag maps `GOODY2` to its IP address (`10.5.5.25`) so the container can reach SQL Server. SQL auth credentials are passed via the connection string environment variable instead of Integrated Security.

### 3 — Open the UI

Navigate to **http://localhost:8080** in any browser.

### Stop the container

```bash
docker stop intacct-web
```

The `--rm` flag means the container is automatically removed when stopped. Run the same `docker run` command again to restart.

### View logs

```bash
docker logs intacct-web
```

---

## Configuration Reference

All settings follow ASP.NET Core's configuration hierarchy — `appsettings.json` for local development, environment variables for Docker. Note the double-underscore separator for environment variables: `IntAcct__ClientId` maps to config key `IntAcct:ClientId`.

| Key | Description |
|-----|-------------|
| `ConnectionStrings:IntAcct` | SQL Server connection string for the `IntAcct` database on `GOODY2` |
| `IntAcct:ClientId` | Intacct OAuth2 client ID |
| `IntAcct:ClientSecret` | Intacct OAuth2 client secret |
| `IntAcct:EntityId` | Intacct entity identifier (e.g. `GOODY`) |
| `IntAcct:TokenUrl` | OAuth2 token endpoint — defaults to the standard Intacct URL |
| `IntAcct:QueryUrl` | Customer list query endpoint — defaults to the standard Intacct URL |
| `IntAcct:ObjectsBaseUrl` | Base URL for individual object lookups — defaults to the standard Intacct URL |
