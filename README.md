# Microsoft Entra OIDC + SCIM Lab — .NET 8 Azure Functions

This branch converts the original Node/Express lab to **.NET 8 isolated Azure Functions** while keeping the same browser UI and the same public endpoints.

## What this lab demonstrates

- **OIDC** interactive sign-in with Microsoft Entra ID using the authorization-code flow.
- **SCIM 2.0** user provisioning from Microsoft Entra ID.
- Azure Table Storage for persistent SCIM users and OIDC sessions.
- Deployment to an Azure Function App.

## Main endpoints

| Purpose | Endpoint |
|---|---|
| Dashboard | `/` |
| Health | `/health` |
| OIDC login | `/auth/login` |
| OIDC callback | `/auth/redirect` |
| Logout | `/auth/logout` |
| Current signed-in user | `/api/me` |
| Dashboard SCIM users | `/api/scim-users` |
| SCIM base URL | `/scim/v2` |
| SCIM users | `/scim/v2/Users` |
| SCIM service config | `/scim/v2/ServiceProviderConfig` |
| SCIM resource types | `/scim/v2/ResourceTypes` |
| SCIM schemas | `/scim/v2/Schemas` |

## Project structure

```text
EntraOidcScimLab.csproj
Program.cs
host.json
Functions/
  WebFunctions.cs
  ScimFunctions.cs
Models/
  Entities.cs
Services/
  LabConfig.cs
  LabStorage.cs
  CookieSigner.cs
  HttpHelpers.cs
  OidcService.cs
public/
  index.html
  app.js
  styles.css
```

## Local configuration

Copy `local.settings.json.example` to `local.settings.json` and set:

```text
AzureWebJobsStorage=<Azurite or Azure Storage connection>
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
BASE_URL=http://localhost:7071
ENTRA_TENANT_ID=<tenant-guid>
ENTRA_CLIENT_ID=<application-client-id>
ENTRA_CLIENT_SECRET=<client-secret-value>
SESSION_SECRET=<long-random-secret>
SCIM_BEARER_TOKEN=<long-random-token>
```

Never commit real secrets.

## Azure Function App settings

Configure these under **Function App > Configuration / Environment variables**:

```text
FUNCTIONS_WORKER_RUNTIME=dotnet-isolated
BASE_URL=https://YOUR-FUNCTION-APP.azurewebsites.net
ENTRA_TENANT_ID=<tenant-guid>
ENTRA_CLIENT_ID=<application-client-id>
ENTRA_CLIENT_SECRET=<client-secret-value>
SESSION_SECRET=<long-random-secret>
SCIM_BEARER_TOKEN=<long-random-token>
```

`AzureWebJobsStorage` is already required by Azure Functions. This lab also uses that Storage account to create two tables automatically:

- `ScimUsers`
- `OidcSessions`

## Entra OIDC setup

Create an Entra **App registration** and configure a Web redirect URI:

```text
https://YOUR-FUNCTION-APP.azurewebsites.net/auth/redirect
```

Copy the Application (client) ID and Directory (tenant) ID into the Function App settings and create a client secret for `ENTRA_CLIENT_SECRET`.

The sign-in flow is:

```text
Browser -> /auth/login -> Microsoft Entra ID
        <- authorization code -> /auth/redirect
        -> validated ID token -> signed session cookie
```

The app validates the ID-token issuer, audience, signature, lifetime and OIDC nonce.

## Entra SCIM setup

Create or use an Entra Enterprise Application and configure automatic provisioning.

Tenant URL:

```text
https://YOUR-FUNCTION-APP.azurewebsites.net/scim/v2
```

Secret Token:

```text
<same value as SCIM_BEARER_TOKEN>
```

The lab supports user operations:

- `GET /scim/v2/Users`
- `GET /scim/v2/Users?filter=userName eq "user@domain.com"`
- `POST /scim/v2/Users`
- `GET /scim/v2/Users/{id}`
- `PUT /scim/v2/Users/{id}`
- `PATCH /scim/v2/Users/{id}`
- `DELETE /scim/v2/Users/{id}`

Group provisioning is not implemented in this lab.

## Build and publish

```powershell
dotnet restore
dotnet build --configuration Release
dotnet publish --configuration Release --output ./publish
```

Deploy the contents of `publish` as a ZIP package to a **.NET 8 isolated Azure Function App**.

## Branch

The .NET conversion is maintained in:

```text
dotnet-functions
```

The original Node version remains on `main` until the .NET version is tested and you decide to merge it.
