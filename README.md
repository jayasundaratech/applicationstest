# Microsoft Entra OIDC + SCIM Lab

A small dynamic Node.js web application for testing two different Microsoft Entra ID identity flows:

- **OpenID Connect (OIDC)** — interactive user sign-in to the web application.
- **SCIM 2.0** — automatic user provisioning, updates, disabling and deletion from Microsoft Entra ID into the application.

The dashboard shows the signed-in user's Entra ID token claims and the users provisioned through SCIM.

## Architecture

```text
OIDC authentication

Browser -> Web App -> Microsoft Entra ID
                    <- ID token / authorization code
Browser <- Web App session

SCIM provisioning

Microsoft Entra Provisioning Service -> /scim/v2/Users -> App user store
              POST / GET / PATCH / PUT / DELETE
```

## Application endpoints

| Purpose | Endpoint |
|---|---|
| Home/dashboard | `/` |
| Health | `/health` |
| OIDC login | `/auth/login` |
| OIDC callback | `/auth/redirect` |
| Logout | `/auth/logout` |
| SCIM base URL | `/scim/v2` |
| SCIM Users | `/scim/v2/Users` |
| SCIM ServiceProviderConfig | `/scim/v2/ServiceProviderConfig` |
| SCIM ResourceTypes | `/scim/v2/ResourceTypes` |
| SCIM Schemas | `/scim/v2/Schemas` |

## 1. Run locally

Requirements: Node.js 20 or newer.

```bash
npm install
cp .env.example .env
npm start
```

Open `http://localhost:3000`.

The dashboard works before Entra is configured and shows which settings are missing.

## 2. Deploy publicly

OIDC can be developed locally, but Microsoft Entra SCIM provisioning needs an HTTPS endpoint that Entra can reach. Azure App Service is a simple option for this lab.

Create a Linux Node.js Azure Web App and deploy this GitHub repository. Set the startup command to `npm start` if Azure does not detect it automatically.

After deployment, assume the public address is:

```text
https://YOUR-APP.azurewebsites.net
```

Set these application settings in the Azure Web App:

```text
NODE_ENV=production
BASE_URL=https://YOUR-APP.azurewebsites.net
ENTRA_TENANT_ID=<tenant-guid>
ENTRA_CLIENT_ID=<application-client-id>
ENTRA_CLIENT_SECRET=<client-secret-value>
SESSION_SECRET=<long-random-secret>
SCIM_BEARER_TOKEN=<long-random-secret-token>
```

Never commit real secrets to this repository.

## 3. Configure OIDC in Microsoft Entra ID

### Create the app registration

1. Open **Microsoft Entra admin center**.
2. Go to **Identity > Applications > App registrations**.
3. Select **New registration**.
4. Name it, for example, `Entra OIDC SCIM Lab`.
5. For a company-only lab, select **Accounts in this organizational directory only**.
6. Under **Redirect URI**, select **Web**.
7. Enter:

```text
https://YOUR-APP.azurewebsites.net/auth/redirect
```

8. Create the registration.

### Copy IDs

From **Overview**, copy:

- **Application (client) ID** -> `ENTRA_CLIENT_ID`
- **Directory (tenant) ID** -> `ENTRA_TENANT_ID`

### Create client secret

1. Open **Certificates & secrets**.
2. Select **New client secret**.
3. Create the secret.
4. Copy the **secret VALUE** immediately.
5. Store it as `ENTRA_CLIENT_SECRET` in Azure App Service configuration.

Do not use the secret ID.

### Authentication settings

Under **Authentication**, confirm this Web redirect URI exists:

```text
https://YOUR-APP.azurewebsites.net/auth/redirect
```

This application uses the OIDC authorization-code flow through MSAL Node.

## 4. Test OIDC

1. Browse to the deployed app.
2. The OIDC status should show **Configured**.
3. Select **Sign in with Entra ID**.
4. Sign in with an Entra user.
5. After the callback, the dashboard displays:
   - user name
   - username
   - tenant ID (`tid`)
   - object ID (`oid`)
   - ID-token claims

This demonstrates interactive OIDC authentication.

## 5. Configure SCIM provisioning in Microsoft Entra ID

SCIM is a separate flow from OIDC. OIDC signs a human into the application. SCIM synchronizes user lifecycle data from Entra into the application's user store.

### Create or use an Enterprise Application

For a clean lab, create a separate non-gallery Enterprise Application for provisioning:

1. Go to **Identity > Applications > Enterprise applications**.
2. Select **New application**.
3. Select **Create your own application**.
4. Choose the option to integrate another application not found in the gallery.
5. Name it, for example, `SCIM Provisioning Lab`.

### Configure provisioning

1. Open the Enterprise Application.
2. Go to **Provisioning**.
3. Configure automatic provisioning.
4. Set **Tenant URL** to:

```text
https://YOUR-APP.azurewebsites.net/scim/v2
```

5. Set **Secret Token** to exactly the same value configured in Azure as:

```text
SCIM_BEARER_TOKEN
```

6. Select **Test Connection**.

The app accepts the token as:

```http
Authorization: Bearer <SCIM_BEARER_TOKEN>
```

### User attribute mapping

For the first lab, keep the common user mappings, especially:

- `userName`
- `active`
- `displayName`
- `name.givenName`
- `name.familyName`
- `emails`
- `externalId`

The endpoint is intentionally focused on **users**. Group provisioning is not implemented in this first version.

## 6. Test SCIM with Provision on demand

1. Assign a test user to the Enterprise Application.
2. Open **Provisioning**.
3. Use **Provision on demand** for that user.
4. Entra normally performs a filter lookup first, similar to:

```http
GET /scim/v2/Users?filter=userName eq "user@contoso.com"
```

5. If the user does not exist, Entra creates it with:

```http
POST /scim/v2/Users
```

6. Sign into the web app and select **Refresh** under SCIM provisioning.
7. The provisioned account should appear in the dashboard.

## 7. Test an update

Change a mapped Entra user attribute and run provisioning again. Entra can send a `PATCH` request to the matching SCIM user. The application records the updated user and last-modified time.

## 8. Test deprovisioning

Unassign the user or otherwise trigger deprovisioning according to your Enterprise Application provisioning configuration. Entra commonly sends a SCIM update that sets:

```json
{
  "Operations": [
    {
      "op": "Replace",
      "path": "active",
      "value": false
    }
  ]
}
```

The dashboard will then show that SCIM account as **Disabled**.

## SCIM support in this lab

Supported user operations:

- `GET /Users`
- `GET /Users?filter=userName eq "..."`
- `GET /Users/{id}`
- `POST /Users`
- `PUT /Users/{id}`
- `PATCH /Users/{id}`
- `DELETE /Users/{id}`

Supported discovery endpoints:

- `/ServiceProviderConfig`
- `/ResourceTypes`
- `/Schemas`

## Important lab limitation

SCIM users are stored in `data/scim-users.json`. This is easy to understand for a lab, but it is **not a production database design**. For production, replace it with Azure SQL, Cosmos DB, PostgreSQL or another persistent database and use a production-grade SCIM implementation and authorization model.

## Useful Microsoft documentation

- OIDC / Microsoft identity platform: https://learn.microsoft.com/entra/identity-platform/v2-protocols-oidc
- Web application identity flows: https://learn.microsoft.com/entra/identity-platform/v2-app-types
- Build a SCIM endpoint for Entra provisioning: https://learn.microsoft.com/entra/identity/app-provisioning/use-scim-to-build-users-and-groups-endpoints
- SCIM support in Microsoft Entra ID: https://learn.microsoft.com/entra/identity/app-provisioning/scim-support-in-entra-id
