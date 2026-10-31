const $ = (id) => document.getElementById(id);

let currentUser = null;
let scimUsersCache = [];

function setStatus(dot, label, ok, yesText, noText) {
  $(dot).classList.remove('ok', 'bad');
  $(dot).classList.add(ok ? 'ok' : 'bad');
  $(label).textContent = ok ? yesText : noText;
}

function escapeHtml(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}

function normalize(value) {
  return String(value ?? '').trim().toLowerCase();
}

function setDecisionCheck(id, ok) {
  const el = $(id);
  el.classList.remove('pass', 'fail');
  el.classList.add(ok ? 'pass' : 'fail');
  el.querySelector('.check-icon').textContent = ok ? '✓' : '×';
}

function evaluateAccess() {
  const authenticated = Boolean(currentUser?.username);
  const signedInName = normalize(currentUser?.username);

  const provisionedUser = scimUsersCache.find((user) =>
    normalize(user.userName) === signedInName
  );

  const activeProvisioned = Boolean(provisionedUser?.active);

  setDecisionCheck('authCheck', authenticated);
  setDecisionCheck('provisionCheck', activeProvisioned);

  const pill = $('decisionPill');
  const result = $('decisionResult');

  pill.classList.remove('pending', 'allowed', 'denied');

  if (!authenticated) {
    pill.textContent = 'Waiting';
    pill.classList.add('pending');
    result.className = 'decision-result';
    result.innerHTML = '<strong>Sign in first</strong><small>OIDC authentication is required before access can be evaluated.</small>';
    return;
  }

  if (activeProvisioned) {
    pill.textContent = 'Access allowed';
    pill.classList.add('allowed');
    result.className = 'decision-result allowed';
    result.innerHTML = `<strong>ALLOW</strong><small>${escapeHtml(currentUser.username)} is authenticated by OIDC and active in SCIM.</small>`;
  } else {
    pill.textContent = 'Access denied';
    pill.classList.add('denied');
    result.className = 'decision-result denied';
    result.innerHTML = `<strong>DENY</strong><small>${escapeHtml(currentUser.username)} authenticated successfully, but no active SCIM account was found.</small>`;
  }
}

async function loadConfig() {
  const response = await fetch('/api/config');
  const config = await response.json();

  setStatus('oidcDot', 'oidcStatus', config.oidcConfigured, 'Configured', 'Needs Entra settings');
  setStatus('scimDot', 'scimStatus', config.scimConfigured, 'Configured', 'Needs bearer token');

  $('redirectUri').textContent = config.redirectUri;
  $('scimBaseUrl').textContent = config.scimBaseUrl;
  $('healthUrl').textContent = `${config.baseUrl}/health`;

  if (!config.oidcConfigured) {
    $('authActions').innerHTML = '<span class="badge">Configure Entra app settings first</span>';
  }
}

async function loadMe() {
  const response = await fetch('/api/me');
  const data = await response.json();

  if (!data.authenticated) {
    currentUser = null;
    evaluateAccess();
    return false;
  }

  currentUser = data.user;

  $('loginBadge').textContent = 'Signed in';
  $('loginBadge').classList.add('ok');
  $('authActions').innerHTML = '<a class="button ghost" href="/auth/logout">Sign out</a>';
  $('userPanel').classList.remove('empty-state');
  $('userPanel').innerHTML = `
    <div class="identity-banner">
      <div class="avatar">${escapeHtml((data.user.name || data.user.username || '?').charAt(0).toUpperCase())}</div>
      <div>
        <span>Authenticated by Microsoft Entra ID</span>
        <strong>${escapeHtml(data.user.name || data.user.username)}</strong>
      </div>
    </div>
    <div class="user-summary">
      <div><span>Username</span><strong>${escapeHtml(data.user.username)}</strong></div>
      <div><span>Tenant ID</span><strong>${escapeHtml(data.user.tenantId)}</strong></div>
      <div><span>Object ID</span><strong>${escapeHtml(data.user.objectId)}</strong></div>
      <div><span>Authentication</span><strong>OIDC</strong></div>
    </div>
    <details>
      <summary>View ID-token claims</summary>
      <pre class="claims">${escapeHtml(JSON.stringify(data.claims, null, 2))}</pre>
    </details>
  `;

  evaluateAccess();
  return true;
}

async function loadScimUsers() {
  const container = $('scimUsers');
  const response = await fetch('/api/scim-users');

  if (response.status === 401) {
    scimUsersCache = [];
    container.className = 'empty-state';
    container.textContent = 'Sign in first to view SCIM-provisioned users.';
    evaluateAccess();
    return;
  }

  const users = await response.json();
  scimUsersCache = users;

  if (!users.length) {
    container.className = 'empty-state';
    container.textContent = 'No SCIM users have been provisioned yet.';
    evaluateAccess();
    return;
  }

  container.className = 'user-list';
  container.innerHTML = users.map((user) => {
    const isCurrent = currentUser && normalize(user.userName) === normalize(currentUser.username);
    return `
      <div class="user-row ${isCurrent ? 'current' : ''}">
        <div class="user-row-main">
          <div class="mini-avatar">${escapeHtml((user.displayName || user.userName || '?').charAt(0).toUpperCase())}</div>
          <div>
            <strong>${escapeHtml(user.displayName || user.userName)}</strong>
            <small>${escapeHtml(user.userName)}</small>
            ${isCurrent ? '<em>Matches current OIDC user</em>' : ''}
          </div>
        </div>
        <span class="state ${user.active ? 'active' : 'disabled'}">${user.active ? 'Active' : 'Disabled'}</span>
      </div>
    `;
  }).join('');

  evaluateAccess();
}

$('refreshUsers').addEventListener('click', loadScimUsers);

(async () => {
  try {
    await loadConfig();
    const signedIn = await loadMe();
    if (signedIn) await loadScimUsers();
  } catch (error) {
    console.error(error);
    $('decisionResult').textContent = 'The demo could not load all identity data. Check the browser console and Function App logs.';
  }
})();
