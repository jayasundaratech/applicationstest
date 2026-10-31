# Run the Entra OIDC + SCIM Lab entirely in GitHub Codespaces

This option does not use Azure App Service.

## 1. Create a Codespace

1. Open the repository: `jayasundaratech/applicationstest`.
2. Click **Code**.
3. Open the **Codespaces** tab.
4. Click **Create codespace on main**.
5. The dev container installs the Node.js dependencies automatically.

## 2. Configure Entra values

In the Codespace terminal:

```bash
cp .env.example .env
```

Edit `.env` and set:

```text
ENTRA_TENANT_ID=<your tenant id>
ENTRA_CLIENT_ID=<your app registration client id>
ENTRA_CLIENT_SECRET=<your app registration client secret>
SESSION_SECRET=<any long random value>
SCIM_BEARER_TOKEN=<any long random value>
```

`BASE_URL` can be left blank. The application will derive its URL from the forwarded Codespaces request.

Never commit the `.env` file.

## 3. Start the web application

```bash
npm start
```

The app listens on port `3000`.

## 4. Make the Codespaces port public

1. Open the **PORTS** tab in the Codespace.
2. Find port **3000 – Entra Identity Lab**.
3. Right-click port 3000.
4. Choose **Port Visibility**.
5. Choose **Public**.
6. Copy the forwarded URL.

Example format:

```text
https://<codespace-name>-3000.app.github.dev
```

A public forwarded port can return to private after a Codespace restart, so check the PORTS tab before testing Entra provisioning again.

## 5. Configure OIDC in Microsoft Entra ID

Create an App Registration for a Web application.

Use this Redirect URI:

```text
https://<your-codespace-url>/auth/redirect
```

Example:

```text
https://<codespace-name>-3000.app.github.dev/auth/redirect
```

Create a client secret and put the secret value into `ENTRA_CLIENT_SECRET` in the Codespace `.env` file.

Then open the application and select **Sign in with Microsoft Entra ID**.

## 6. Configure SCIM provisioning in Microsoft Entra ID

Create or use an Enterprise Application and configure automatic provisioning.

Tenant URL:

```text
https://<your-codespace-url>/scim/v2
```

Secret Token:

Use the exact value configured as:

```text
SCIM_BEARER_TOKEN
```

The app supports the main user-lifecycle operations used by the lab:

- Find users
- Create users
- Update users
- Disable users
- Delete users

## Architecture

```text
User Browser
    |
    | OIDC sign-in
    v
Microsoft Entra ID
    |
    v
GitHub Codespace public port 3000
    |
    +--> Node.js web application
    |
    +--> /auth/*           OIDC
    |
    +--> /scim/v2/Users   SCIM

Microsoft Entra Provisioning Service
    |
    | SCIM HTTPS requests
    v
GitHub Codespace public port 3000
```

## Important

GitHub Pages can run the static OIDC demonstration UI, but it cannot host a real SCIM API because Pages does not execute server-side Node.js code. GitHub Codespaces can run the full Node.js application, so use Codespaces when testing both OIDC and actual SCIM provisioning.
