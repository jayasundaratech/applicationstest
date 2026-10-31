const $ = (id) => document.getElementById(id);

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

  if (!data.authenticated) return false;

  $('loginBadge').textContent = 'Signed in';
  $('loginBadge').classList.add('ok');
  $('authActions').innerHTML = '<a class="button ghost" href="/auth/logout">Sign out</a>';
  $('userPanel').classList.remove('empty-state');
  $('userPanel').innerHTML = `
    <div class="user-summary">
      <div><span>Name</span><strong>${escapeHtml(data.user.name)}</strong></div>
      <div><span>Username</span><strong>${escapeHtml(data.user.username)}</strong></div>
      <div><span>Tenant ID</span><strong>${escapeHtml(data.user.tenantId)}</strong></div>
      <div><span>Object ID</span><strong>${escapeHtml(data.user.objectId)}</strong></div>
    </div>
    <p class="kicker">ID token claims</p>
    <pre class="claims">${escapeHtml(JSON.stringify(data.claims, null, 2))}</pre>
  `;
  return true;
}

async function loadScimUsers() {
  const container = $('scimUsers');
  const response = await fetch('/api/scim-users');

  if (response.status === 401) {
    container.className = 'empty-state';
    container.textContent = 'Sign in first, then provision a user from Entra.';
    return;
  }

  const users = await response.json();
  if (!users.length) {
    container.className = 'empty-state';
    container.textContent = 'No SCIM users yet. Assign a user in Entra and run Provision on demand.';
    return;
  }

  container.className = 'user-list';
  container.innerHTML = users.map((user) => `
    <div class="user-row">
      <div>
        <strong>${escapeHtml(user.displayName || user.userName)}</strong>
        <small>${escapeHtml(user.userName)}</small>
      </div>
      <span class="state ${user.active ? 'active' : 'disabled'}">${user.active ? 'Active' : 'Disabled'}</span>
    </div>
  `).join('');
}

$('refreshUsers').addEventListener('click', loadScimUsers);

(async () => {
  try {
    await loadConfig();
    const signedIn = await loadMe();
    if (signedIn) await loadScimUsers();
  } catch (error) {
    console.error(error);
  }
})();
