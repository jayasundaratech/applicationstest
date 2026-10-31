require('dotenv').config();

const express = require('express');
const session = require('express-session');
const msal = require('@azure/msal-node');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

const app = express();
const PORT = process.env.PORT || 3000;
const DATA_FILE = path.join(__dirname, 'data', 'scim-users.json');

app.set('trust proxy', 1);
app.use(express.json({ type: ['application/json', 'application/scim+json'] }));
app.use(express.urlencoded({ extended: false }));
app.use(
  session({
    secret: process.env.SESSION_SECRET || 'dev-only-change-me',
    resave: false,
    saveUninitialized: false,
    cookie: {
      httpOnly: true,
      sameSite: 'lax',
      secure: process.env.NODE_ENV === 'production',
      maxAge: 60 * 60 * 1000,
    },
  })
);
app.use(express.static(path.join(__dirname, 'public')));

function oidcConfigured() {
  return Boolean(process.env.ENTRA_TENANT_ID && process.env.ENTRA_CLIENT_ID && process.env.ENTRA_CLIENT_SECRET);
}

function getBaseUrl(req) {
  return (process.env.BASE_URL || `${req.protocol}://${req.get('host')}`).replace(/\/$/, '');
}

function getMsalClient() {
  if (!oidcConfigured()) return null;
  return new msal.ConfidentialClientApplication({
    auth: {
      clientId: process.env.ENTRA_CLIENT_ID,
      authority: `https://login.microsoftonline.com/${process.env.ENTRA_TENANT_ID}`,
      clientSecret: process.env.ENTRA_CLIENT_SECRET,
    },
    system: {
      loggerOptions: {
        loggerCallback: () => {},
        piiLoggingEnabled: false,
      },
    },
  });
}

function ensureDataFile() {
  fs.mkdirSync(path.dirname(DATA_FILE), { recursive: true });
  if (!fs.existsSync(DATA_FILE)) fs.writeFileSync(DATA_FILE, '[]\n', 'utf8');
}

function readUsers() {
  ensureDataFile();
  try {
    return JSON.parse(fs.readFileSync(DATA_FILE, 'utf8'));
  } catch {
    return [];
  }
}

function writeUsers(users) {
  ensureDataFile();
  fs.writeFileSync(DATA_FILE, `${JSON.stringify(users, null, 2)}\n`, 'utf8');
}

function scimError(res, status, detail) {
  return res.status(status).type('application/scim+json').json({
    schemas: ['urn:ietf:params:scim:api:messages:2.0:Error'],
    status: String(status),
    detail,
  });
}

function requireScimToken(req, res, next) {
  const configured = process.env.SCIM_BEARER_TOKEN;
  if (!configured) return scimError(res, 503, 'SCIM_BEARER_TOKEN is not configured on the server.');

  const header = req.get('authorization') || '';
  const token = header.startsWith('Bearer ') ? header.slice(7) : '';
  const expected = Buffer.from(configured);
  const supplied = Buffer.from(token);
  const valid = expected.length === supplied.length && crypto.timingSafeEqual(expected, supplied);

  if (!valid) return scimError(res, 401, 'Invalid or missing bearer token.');
  next();
}

function requireLogin(req, res, next) {
  if (!req.session.user) return res.status(401).json({ authenticated: false });
  next();
}

function publicUser(user) {
  return {
    id: user.id,
    userName: user.userName,
    displayName: user.displayName,
    active: user.active,
    name: user.name,
    emails: user.emails,
    externalId: user.externalId,
    meta: user.meta,
    schemas: ['urn:ietf:params:scim:schemas:core:2.0:User'],
  };
}

function newScimUser(body, req) {
  const now = new Date().toISOString();
  const id = crypto.randomUUID();
  return {
    schemas: ['urn:ietf:params:scim:schemas:core:2.0:User'],
    id,
    externalId: body.externalId || null,
    userName: body.userName,
    displayName: body.displayName || body.name?.formatted || body.userName,
    active: body.active !== false,
    name: body.name || {},
    emails: Array.isArray(body.emails) ? body.emails : [],
    meta: {
      resourceType: 'User',
      created: now,
      lastModified: now,
      version: `W/\"${Date.now()}\"`,
      location: `${getBaseUrl(req)}/scim/v2/Users/${id}`,
    },
  };
}

function applyScimPatch(user, operations) {
  for (const operation of operations || []) {
    const op = String(operation.op || '').toLowerCase();
    if (!['add', 'replace', 'remove'].includes(op)) continue;

    const patchValue = operation.value;
    const patchPath = operation.path;

    if (!patchPath && patchValue && typeof patchValue === 'object' && !Array.isArray(patchValue)) {
      Object.assign(user, patchValue);
      continue;
    }

    if (!patchPath) continue;
    const simplePath = patchPath.split('[')[0];

    if (op === 'remove') {
      if (simplePath === 'emails') user.emails = [];
      else if (simplePath === 'displayName') user.displayName = '';
      else if (simplePath === 'externalId') user.externalId = null;
      continue;
    }

    if (simplePath === 'active') user.active = patchValue === true || patchValue === 'true';
    else if (simplePath === 'userName') user.userName = patchValue;
    else if (simplePath === 'displayName') user.displayName = patchValue;
    else if (simplePath === 'externalId') user.externalId = patchValue;
    else if (simplePath === 'name') user.name = patchValue || {};
    else if (simplePath === 'emails') user.emails = Array.isArray(patchValue) ? patchValue : user.emails;
  }

  user.meta.lastModified = new Date().toISOString();
  user.meta.version = `W/\"${Date.now()}\"`;
  return user;
}

// ----- General app/API -----
app.get('/health', (req, res) => {
  res.json({ status: 'ok', app: 'Entra OIDC + SCIM Lab', time: new Date().toISOString() });
});

app.get('/api/config', (req, res) => {
  res.json({
    oidcConfigured: oidcConfigured(),
    scimConfigured: Boolean(process.env.SCIM_BEARER_TOKEN),
    baseUrl: getBaseUrl(req),
    redirectUri: `${getBaseUrl(req)}/auth/redirect`,
    scimBaseUrl: `${getBaseUrl(req)}/scim/v2`,
  });
});

app.get('/api/me', (req, res) => {
  if (!req.session.user) return res.json({ authenticated: false });
  res.json({ authenticated: true, user: req.session.user, claims: req.session.claims || {} });
});

app.get('/api/scim-users', requireLogin, (req, res) => {
  res.json(readUsers().map(publicUser));
});

// ----- OIDC / Microsoft Entra ID -----
app.get('/auth/login', async (req, res) => {
  try {
    const client = getMsalClient();
    if (!client) {
      return res.status(503).send('OIDC is not configured yet. Add ENTRA_TENANT_ID, ENTRA_CLIENT_ID and ENTRA_CLIENT_SECRET.');
    }

    const state = crypto.randomUUID();
    req.session.oidcState = state;
    const redirectUri = `${getBaseUrl(req)}/auth/redirect`;
    const url = await client.getAuthCodeUrl({
      scopes: ['openid', 'profile', 'email'],
      redirectUri,
      state,
      prompt: 'select_account',
    });
    res.redirect(url);
  } catch (error) {
    console.error('OIDC login error:', error);
    res.status(500).send('Could not start Entra ID sign-in. Check the app configuration.');
  }
});

app.get('/auth/redirect', async (req, res) => {
  try {
    if (!req.query.code) return res.status(400).send('Authorization code was not returned by Entra ID.');
    if (!req.query.state || req.query.state !== req.session.oidcState) return res.status(400).send('OIDC state validation failed.');

    const client = getMsalClient();
    const result = await client.acquireTokenByCode({
      code: req.query.code,
      scopes: ['openid', 'profile', 'email'],
      redirectUri: `${getBaseUrl(req)}/auth/redirect`,
    });

    const claims = result.idTokenClaims || {};
    req.session.user = {
      name: result.account?.name || claims.name || 'Entra user',
      username: result.account?.username || claims.preferred_username || claims.email || '',
      homeAccountId: result.account?.homeAccountId || '',
      tenantId: claims.tid || '',
      objectId: claims.oid || '',
    };
    req.session.claims = claims;
    delete req.session.oidcState;
    res.redirect('/');
  } catch (error) {
    console.error('OIDC callback error:', error);
    res.status(500).send('Entra ID sign-in failed. Check tenant ID, client ID, secret and redirect URI.');
  }
});

app.get('/auth/logout', (req, res) => {
  const baseUrl = getBaseUrl(req);
  const tenant = process.env.ENTRA_TENANT_ID;
  req.session.destroy(() => {
    if (!tenant) return res.redirect('/');
    const logout = new URL(`https://login.microsoftonline.com/${tenant}/oauth2/v2.0/logout`);
    logout.searchParams.set('post_logout_redirect_uri', baseUrl);
    res.redirect(logout.toString());
  });
});

// ----- SCIM 2.0 -----
app.use('/scim/v2', requireScimToken);

app.get('/scim/v2/ServiceProviderConfig', (req, res) => {
  res.type('application/scim+json').json({
    schemas: ['urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig'],
    patch: { supported: true },
    bulk: { supported: false, maxOperations: 0, maxPayloadSize: 0 },
    filter: { supported: true, maxResults: 200 },
    changePassword: { supported: false },
    sort: { supported: false },
    etag: { supported: false },
    authenticationSchemes: [{ type: 'oauthbearertoken', name: 'Bearer Token', description: 'Static bearer token for this lab.' }],
  });
});

app.get('/scim/v2/ResourceTypes', (req, res) => {
  res.type('application/scim+json').json({
    schemas: ['urn:ietf:params:scim:api:messages:2.0:ListResponse'],
    totalResults: 1,
    Resources: [{ id: 'User', name: 'User', endpoint: '/Users', schema: 'urn:ietf:params:scim:schemas:core:2.0:User' }],
  });
});

app.get('/scim/v2/Schemas', (req, res) => {
  res.type('application/scim+json').json({
    schemas: ['urn:ietf:params:scim:api:messages:2.0:ListResponse'],
    totalResults: 1,
    Resources: [{ id: 'urn:ietf:params:scim:schemas:core:2.0:User', name: 'User', description: 'SCIM core user schema for the Entra lab.' }],
  });
});

app.get('/scim/v2/Users', (req, res) => {
  let users = readUsers();
  const filter = req.query.filter;

  if (filter) {
    const match = String(filter).match(/^userName\s+eq\s+"([^"]+)"$/i);
    if (match) users = users.filter((user) => user.userName?.toLowerCase() === match[1].toLowerCase());
    else return scimError(res, 400, 'This lab currently supports filters in the form: userName eq "user@domain.com"');
  }

  res.type('application/scim+json').json({
    schemas: ['urn:ietf:params:scim:api:messages:2.0:ListResponse'],
    totalResults: users.length,
    startIndex: 1,
    itemsPerPage: users.length,
    Resources: users.map(publicUser),
  });
});

app.post('/scim/v2/Users', (req, res) => {
  if (!req.body.userName) return scimError(res, 400, 'userName is required.');

  const users = readUsers();
  if (users.some((user) => user.userName?.toLowerCase() === req.body.userName.toLowerCase())) {
    return scimError(res, 409, 'A user with this userName already exists.');
  }

  const user = newScimUser(req.body, req);
  users.push(user);
  writeUsers(users);
  res.set('Location', user.meta.location).status(201).type('application/scim+json').json(publicUser(user));
});

app.get('/scim/v2/Users/:id', (req, res) => {
  const user = readUsers().find((item) => item.id === req.params.id);
  if (!user) return scimError(res, 404, 'User not found.');
  res.type('application/scim+json').json(publicUser(user));
});

app.put('/scim/v2/Users/:id', (req, res) => {
  const users = readUsers();
  const index = users.findIndex((item) => item.id === req.params.id);
  if (index === -1) return scimError(res, 404, 'User not found.');
  if (!req.body.userName) return scimError(res, 400, 'userName is required.');

  const existing = users[index];
  users[index] = {
    ...existing,
    ...req.body,
    id: existing.id,
    schemas: ['urn:ietf:params:scim:schemas:core:2.0:User'],
    meta: {
      ...existing.meta,
      lastModified: new Date().toISOString(),
      version: `W/\"${Date.now()}\"`,
    },
  };
  writeUsers(users);
  res.type('application/scim+json').json(publicUser(users[index]));
});

app.patch('/scim/v2/Users/:id', (req, res) => {
  const users = readUsers();
  const index = users.findIndex((item) => item.id === req.params.id);
  if (index === -1) return scimError(res, 404, 'User not found.');

  users[index] = applyScimPatch(users[index], req.body.Operations);
  writeUsers(users);
  res.type('application/scim+json').json(publicUser(users[index]));
});

app.delete('/scim/v2/Users/:id', (req, res) => {
  const users = readUsers();
  const index = users.findIndex((item) => item.id === req.params.id);
  if (index === -1) return scimError(res, 404, 'User not found.');
  users.splice(index, 1);
  writeUsers(users);
  res.status(204).end();
});

app.listen(PORT, () => {
  ensureDataFile();
  console.log(`Entra OIDC + SCIM Lab listening on port ${PORT}`);
});
