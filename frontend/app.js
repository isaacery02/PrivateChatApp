"use strict";

// ── Configuration ─────────────────────────────────────────────────────────────
const API_BASE = "";

// ── Colour palette for generated avatars ─────────────────────────────────────
const AVATAR_COLORS = [
  "#5865f2","#eb459e","#ed4245","#fe7302","#faa61a",
  "#3ba55c","#1abc9c","#0099e1","#9b59b6","#607d8b"
];

// ── State ─────────────────────────────────────────────────────────────────────
// JWT is stored in an HttpOnly cookie; in-memory `token` is only used as a
// fallback for the SignalR accessTokenFactory (WebSocket query string).
let token           = null;
let username        = localStorage.getItem("chatUsername");
let userId          = localStorage.getItem("chatUserId");
let userDisplayName = localStorage.getItem("chatDisplayName") || username;
let userAvatarColor = localStorage.getItem("chatAvatarColor") || "#5865f2";
let userAvatarFileId= localStorage.getItem("chatAvatarFileId") || null;
let connection      = null;
let activeRoom      = null;           // { id, name, description, isPrivate, memberIds }
const joinedRooms   = new Set();
const onlineUsers   = new Map();      // userId → username
const typingTimers  = {};             // username → timer id

// Context-menu state
let ctxMessageId  = null;
let ctxRoomId     = null;

// Edit-modal state
let editMessageId = null;
let editRoomId    = null;

// 2FA / TOTP state
let userTotpEnabled     = localStorage.getItem("chatTotpEnabled") === "1";   // reflects whether current user has TOTP active
let totpTempToken       = null;    // pending JWT held between password step and code step
let totpSetupTempToken  = null;    // pending JWT for login-time forced first-time setup

// Voice recording state
let mediaRecorder  = null;
let audioChunks    = [];
let isRecording    = false;
let recTimerRef    = null;

// DM sidebar
const dmConversations = new Map(); // uid → { username, roomId }

// Scroll-to-bottom / unread-while-scrolled tracking
let scrollUnreadCount = 0;        // messages received while not at bottom

// Unread badge counts per room
const unreadCounts = new Map();   // roomId → number

// Offline send queue
const sendQueue = [];             // [{ roomId, content }]

// Reply-to state
let replyToMessage = null;        // { id, senderDisplayName, content } | null

// @mention autocomplete
let mentionStart = -1;            // cursor index where '@' was typed
let mentionDropdownVisible = false;
let mentionItems = [];            // current mention candidates
let mentionIndex = -1;            // keyboard-selected index

// ── JWT expiry helper ────────────────────────────────────────────────────────
function isTokenExpired(jwt) {
  try {
    const payload = JSON.parse(atob(jwt.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
    return payload.exp && payload.exp < Date.now() / 1000;
  } catch { return true; }
}

// ── Bootstrap ─────────────────────────────────────────────────────────────────
document.addEventListener("DOMContentLoaded", () => {
  // JWT lives in an HttpOnly cookie now, so we can't inspect it from JS.
  // If profile data is still in localStorage, attempt to restore the session.
  // The first authenticated API call will validate the cookie; a 401 triggers logout.
  if (username && userId) {
    showApp();
  }

  initSwarm();

  document.getElementById("login-form").addEventListener("submit", (e) => {
    e.preventDefault();
    login();
  });

  const msgInput = document.getElementById("message-input");

  msgInput.addEventListener("keydown", (e) => {
    // @mention autocomplete keyboard navigation
    if (mentionDropdownVisible) {
      if (e.key === "ArrowDown") { e.preventDefault(); moveMentionSelection(1); return; }
      if (e.key === "ArrowUp")   { e.preventDefault(); moveMentionSelection(-1); return; }
      if (e.key === "Enter" || e.key === "Tab") {
        if (mentionIndex >= 0) { e.preventDefault(); acceptMention(mentionItems[mentionIndex]); return; }
      }
      if (e.key === "Escape") { hideMentionDropdown(); return; }
    }
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendMessage(); }
    else broadcastTyping();
  });

  msgInput.addEventListener("input", onMentionInput);

  // Image paste from clipboard
  msgInput.addEventListener("paste", (e) => {
    const items = e.clipboardData?.items;
    if (!items) return;
    for (const item of items) {
      if (item.kind === "file" && item.type.startsWith("image/")) {
        e.preventDefault();
        const file = item.getAsFile();
        if (file) uploadFile(file);
        return;
      }
    }
  });

  // Scroll-to-bottom button wiring
  document.getElementById("messages").addEventListener("scroll", onMessagesScroll);
  document.getElementById("scroll-bottom-btn").addEventListener("click", scrollToBottom);

  // Reply bar cancel
  document.getElementById("reply-cancel-btn").addEventListener("click", clearReply);

  document.getElementById("new-room-name").addEventListener("keydown", (e) => {
    if (e.key === "Enter") createRoom();
  });

  document.getElementById("file-input").addEventListener("change", (e) => {
    const file = e.target.files?.[0];
    if (file) { uploadFile(file); e.target.value = ""; }
  });

  document.getElementById("avatar-file-input").addEventListener("change", (e) => {
    const file = e.target.files?.[0];
    if (file) uploadAvatar(file);
  });

  document.getElementById("edit-content").addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); submitEdit(); }
    if (e.key === "Escape") hideEditModal();
  });

  document.getElementById("totp-login-code").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); verifyTotp(); }
  });

  document.getElementById("totp-first-code").addEventListener("keydown", (e) => {
    if (e.key === "Enter") { e.preventDefault(); confirmTotpFirst(); }
  });

  // Dismiss context menu on any click outside it
  document.addEventListener("click", (e) => {
    if (!e.target.closest("#context-menu")) hideContextMenu();
  });

  buildColorSwatches();
});

// ── Auth ──────────────────────────────────────────────────────────────────────
async function login() {
  const u = document.getElementById("login-username").value.trim();
  const p = document.getElementById("login-password").value;
  const errEl = document.getElementById("login-error");
  errEl.textContent = "";
  try {
    const res = await apiFetch("/api/auth/login", "POST", { username: u, password: p }, false);
    if (res.status === 429) { errEl.textContent = "Too many attempts. Wait a moment."; return; }
    if (res.status === 401) { errEl.textContent = "Invalid username or password."; return; }
    if (!res.ok) { errEl.textContent = "Login failed. Please try again."; return; }
    const data = await res.json();
    if (data.requiresTotpSetup) {
      totpSetupTempToken = data.tempToken;
      document.getElementById("login-form").classList.add("hidden");
      document.getElementById("totp-setup-step").classList.remove("hidden");
      await loadFirstTotpSetup();
      return;
    }
    if (data.requiresTOTP) {
      totpTempToken = data.tempToken;
      document.getElementById("login-form").classList.add("hidden");
      document.getElementById("totp-step").classList.remove("hidden");
      setTimeout(() => document.getElementById("totp-login-code").focus(), 50);
      return;
    }
    storeAuth(data);
    showApp();
  } catch {
    errEl.textContent = "Cannot reach server. Check your connection.";
  }
}

async function verifyTotp() {
  const code  = document.getElementById("totp-login-code").value.trim();
  const errEl = document.getElementById("totp-login-error");
  errEl.textContent = "";
  if (code.length !== 6) { errEl.textContent = "Enter a 6-digit code."; return; }
  try {
    const res = await apiFetch("/api/auth/verify-totp", "POST",
      { tempToken: totpTempToken, code }, false);
    if (res.status === 429) { errEl.textContent = "Too many attempts. Wait a moment."; return; }
    if (res.status === 401) { errEl.textContent = "Invalid code — try again."; return; }
    if (!res.ok) { errEl.textContent = "Verification failed."; return; }
    totpTempToken = null;
    storeAuth(await res.json());
    document.getElementById("totp-step").classList.add("hidden");
    document.getElementById("login-form").classList.remove("hidden");
    document.getElementById("totp-login-code").value = "";
    showApp();
  } catch {
    errEl.textContent = "Cannot reach server.";
  }
}

function backToLogin() {
  totpTempToken = null;
  document.getElementById("totp-step").classList.add("hidden");
  document.getElementById("login-form").classList.remove("hidden");
  document.getElementById("totp-login-code").value = "";
  document.getElementById("totp-login-error").textContent = "";
}

// ── Login-time forced 2FA first-time setup ────────────────────────────────────
async function loadFirstTotpSetup() {
  const errEl = document.getElementById("totp-first-error");
  errEl.textContent = "";
  document.getElementById("totp-first-secret-box").textContent = "Loading…";
  document.getElementById("totp-first-qr").innerHTML = "";
  try {
    const res = await apiFetch("/api/auth/setup-totp-first", "POST",
      { tempToken: totpSetupTempToken }, false);
    if (!res.ok) { errEl.textContent = "Setup failed. Please log in again."; return; }
    const { secret, uri } = await res.json();
    document.getElementById("totp-first-secret-box").textContent =
      secret.match(/.{1,4}/g).join(" ");
    document.getElementById("totp-first-uri-link").href = uri;
    // Render QR code via qrcodejs library (loaded from CDN)
    /* global QRCode */
    new QRCode(document.getElementById("totp-first-qr"), {
      text: uri, width: 160, height: 160,
      colorDark: "#000000", colorLight: "#ffffff"
    });
    setTimeout(() => document.getElementById("totp-first-code").focus(), 50);
  } catch {
    errEl.textContent = "Network error. Please try again.";
  }
}

async function confirmTotpFirst() {
  const code  = document.getElementById("totp-first-code").value.trim();
  const errEl = document.getElementById("totp-first-error");
  errEl.textContent = "";
  if (code.length !== 6) { errEl.textContent = "Enter the 6-digit code from your app."; return; }
  try {
    const res = await apiFetch("/api/auth/confirm-totp-first", "POST",
      { tempToken: totpSetupTempToken, code }, false);
    if (res.status === 429) { errEl.textContent = "Too many attempts. Wait a moment."; return; }
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      errEl.textContent = body.error ?? "Invalid code — try again.";
      return;
    }
    totpSetupTempToken = null;
    document.getElementById("totp-first-code").value = "";
    document.getElementById("totp-setup-step").classList.add("hidden");
    storeAuth(await res.json());
    showApp();
  } catch {
    errEl.textContent = "Cannot reach server.";
  }
}

function storeAuth({ token: t, username: u, userId: id, displayName, avatarColor, avatarFileId, totpEnabled: te = false }) {
  // JWT is now stored in an HttpOnly cookie by the server — keep only in memory
  // as a fallback for the Authorization header (SignalR accessTokenFactory).
  token = t; username = u; userId = id;
  userDisplayName = displayName || u;
  userAvatarColor = avatarColor || "#5865f2";
  userAvatarFileId = avatarFileId || null;
  userTotpEnabled = te;
  // Only store non-sensitive profile info in localStorage (NOT the JWT)
  localStorage.setItem("chatUsername", u);
  localStorage.setItem("chatUserId", id);
  localStorage.setItem("chatDisplayName", userDisplayName);
  localStorage.setItem("chatAvatarColor", userAvatarColor);
  localStorage.setItem("chatTotpEnabled", te ? "1" : "0");
  if (avatarFileId) localStorage.setItem("chatAvatarFileId", avatarFileId);
}

function logout() {
  // Clear server-side auth cookies (best-effort, don't await)
  fetch(`${API_BASE}/api/auth/logout`, { method: "POST", credentials: "same-origin" }).catch(() => {});
  localStorage.clear();
  token = username = userId = null;
  userTotpEnabled = false;
  totpTempToken = null;
  totpSetupTempToken = null;
  activeRoom = null;
  joinedRooms.clear();
  onlineUsers.clear();
  dmConversations.clear();
  allUsersCache = [];
  _blobUrlCache.forEach(url => URL.revokeObjectURL(url));
  _blobUrlCache.clear();
  if (connection) { connection.stop(); connection = null; }
  document.getElementById("app").classList.add("hidden");
  document.getElementById("auth-overlay").classList.remove("hidden");
  document.getElementById("totp-step").classList.add("hidden");
  document.getElementById("totp-setup-step").classList.add("hidden");
  document.getElementById("login-form").classList.remove("hidden");
  document.getElementById("room-list").innerHTML = "";
  document.getElementById("messages").innerHTML = "";
  document.getElementById("dm-list").innerHTML = `<li class="room-empty" id="dm-list-empty">No conversations yet</li>`;
  document.getElementById("chat-panel").classList.add("hidden");
  document.getElementById("no-room").classList.remove("hidden");
  document.getElementById("login-username").value = "";
  document.getElementById("login-password").value = "";
}

// ── Sidebar (mobile slide-over) ──────────────────────────────────────────────
function openSidebar() {
  document.getElementById("sidebar").classList.add("open");
  document.getElementById("sidebar-backdrop").classList.add("visible");
}
function closeSidebar() {
  document.getElementById("sidebar").classList.remove("open");
  document.getElementById("sidebar-backdrop").classList.remove("visible");
}

// ── App Init ──────────────────────────────────────────────────────────────────
async function showApp() {
  document.getElementById("auth-overlay").classList.add("hidden");
  document.getElementById("app").classList.remove("hidden");
  document.getElementById("current-user").textContent = userDisplayName || username;
  renderSelfAvatar();
  await Promise.all([loadRooms(), connectSignalR()]);
  await loadDmConversations();
  // Request push permission after UI is shown (non-blocking)
  setTimeout(subscribeToPush, 2000);
}

// ── Profile / Avatar ──────────────────────────────────────────────────────────
function renderSelfAvatar() {
  const el = document.getElementById("current-user-avatar");
  if (!el) return;
  renderAvatarInto(el, userDisplayName || username, userAvatarColor, userAvatarFileId);
}

function renderAvatarInto(el, name, color, fileId) {
  el.innerHTML = "";
  el.style.background = safeColor(color);
  if (fileId) {
    const img = document.createElement("img");
    img.alt = name;
    loadImageSrc(fileId).then(src => { if (src) { img.src = src; el.appendChild(img); } });
  } else {
    el.textContent = (name || "?")[0].toUpperCase();
  }
}

function buildColorSwatches() {
  const container = document.getElementById("color-swatches");
  if (!container) return;
  AVATAR_COLORS.forEach(c => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "color-swatch";
    btn.style.background = c;
    btn.dataset.color = c;
    btn.title = c;
    btn.addEventListener("click", () => {
      container.querySelectorAll(".color-swatch").forEach(b => b.classList.remove("selected"));
      btn.classList.add("selected");
      userAvatarColor = c;
      document.getElementById("profile-avatar-preview").style.background = c;
    });
    container.appendChild(btn);
  });
}

function showProfileModal() {
  document.getElementById("profile-display-name").value = userDisplayName || username;
  const preview = document.getElementById("profile-avatar-preview");
  renderAvatarInto(preview, userDisplayName || username, userAvatarColor, userAvatarFileId);
  document.querySelectorAll(".color-swatch").forEach(b => {
    b.classList.toggle("selected", b.dataset.color === userAvatarColor);
  });
  refresh2FAStatus();
  document.getElementById("profile-modal").classList.remove("hidden");
  setTimeout(() => document.getElementById("profile-display-name").focus(), 50);
}

function refresh2FAStatus() {
  document.getElementById("twofa-off").classList.toggle("hidden", userTotpEnabled);
  document.getElementById("twofa-on").classList.toggle("hidden", !userTotpEnabled);
  document.getElementById("twofa-setup").classList.add("hidden");
  document.getElementById("twofa-disable").classList.add("hidden");
}

async function start2FASetup() {
  document.getElementById("twofa-off").classList.add("hidden");
  const setupPanel = document.getElementById("twofa-setup");
  setupPanel.classList.remove("hidden");
  document.getElementById("totp-setup-code").value = "";
  document.getElementById("totp-setup-error").textContent = "";
  document.getElementById("totp-secret-box").textContent = "Loading…";
  const qrEl = document.getElementById("totp-qr-profile");
  if (qrEl) qrEl.innerHTML = "";
  try {
    const res = await apiFetch("/api/users/2fa/setup", "POST");
    if (!res.ok) { document.getElementById("totp-setup-error").textContent = "Setup failed."; return; }
    const { secret, uri } = await res.json();
    // Display secret in groups of 4 for readability
    document.getElementById("totp-secret-box").textContent =
      secret.match(/.{1,4}/g).join(" ");
    document.getElementById("totp-uri-link").href = uri;
    // Render QR code
    if (qrEl) {
      /* global QRCode */
      new QRCode(qrEl, { text: uri, width: 160, height: 160,
                          colorDark: "#000000", colorLight: "#ffffff" });
    }
    setTimeout(() => document.getElementById("totp-setup-code").focus(), 50);
  } catch {
    document.getElementById("totp-setup-error").textContent = "Network error.";
  }
}

function cancel2FASetup() {
  document.getElementById("twofa-setup").classList.add("hidden");
  document.getElementById("twofa-off").classList.remove("hidden");
}

async function confirm2FA() {
  const code  = document.getElementById("totp-setup-code").value.trim();
  const errEl = document.getElementById("totp-setup-error");
  errEl.textContent = "";
  if (code.length !== 6) { errEl.textContent = "Enter a 6-digit code."; return; }
  try {
    const res = await apiFetch("/api/users/2fa/confirm", "POST", { code });
    if (!res.ok) {
      const body = await res.json().catch(() => ({}));
      errEl.textContent = body.error ?? "Invalid code.";
      return;
    }
    userTotpEnabled = true;
    localStorage.setItem("chatTotpEnabled", "1");
    refresh2FAStatus();
  } catch { errEl.textContent = "Network error."; }
}

function show2FADisable() {
  document.getElementById("twofa-on").classList.add("hidden");
  document.getElementById("twofa-disable").classList.remove("hidden");
  document.getElementById("totp-disable-pass").value = "";
  document.getElementById("totp-disable-error").textContent = "";
  setTimeout(() => document.getElementById("totp-disable-pass").focus(), 50);
}

function hide2FADisable() {
  document.getElementById("twofa-disable").classList.add("hidden");
  document.getElementById("twofa-on").classList.remove("hidden");
}

async function disable2FA() {
  const password = document.getElementById("totp-disable-pass").value;
  const errEl = document.getElementById("totp-disable-error");
  errEl.textContent = "";
  if (!password) { errEl.textContent = "Enter your password."; return; }
  try {
    const res = await apiFetch("/api/users/2fa/disable", "POST", { password });
    if (res.status === 401) { errEl.textContent = "Incorrect password."; return; }
    if (!res.ok) { errEl.textContent = "Failed to disable 2FA."; return; }
    userTotpEnabled = false;
    localStorage.setItem("chatTotpEnabled", "0");
    refresh2FAStatus();
  } catch { errEl.textContent = "Network error."; }
}

async function reset2FA() {
  // Prompt for password in the existing disable form
  document.getElementById("twofa-on").classList.add("hidden");
  document.getElementById("twofa-disable").classList.remove("hidden");
  const resetNote = document.getElementById("totp-disable-error");
  resetNote.textContent = "";
  document.getElementById("totp-disable-pass").value = "";
  // Override the confirm button temporarily to do reset + re-setup
  const confirmBtn = document.querySelector("#twofa-disable .btn-danger");
  const originalOnclick = confirmBtn?.getAttribute("onclick");
  if (confirmBtn) {
    confirmBtn.setAttribute("onclick", "");
    confirmBtn.onclick = async () => {
      const password = document.getElementById("totp-disable-pass").value;
      resetNote.textContent = "";
      if (!password) { resetNote.textContent = "Enter your password."; return; }
      try {
        const res = await apiFetch("/api/users/2fa/disable", "POST", { password });
        if (res.status === 401) { resetNote.textContent = "Incorrect password."; return; }
        if (!res.ok) { resetNote.textContent = "Failed to reset 2FA."; return; }
        userTotpEnabled = false;
        // Restore button, then immediately launch new setup
        if (originalOnclick) confirmBtn.setAttribute("onclick", originalOnclick);
        confirmBtn.onclick = null;
        document.getElementById("twofa-disable").classList.add("hidden");
        document.getElementById("totp-disable-pass").value = "";
        start2FASetup();
      } catch { resetNote.textContent = "Network error."; }
    };
  }
  setTimeout(() => document.getElementById("totp-disable-pass").focus(), 50);
}

function hideProfileModal() {
  document.getElementById("profile-modal").classList.add("hidden");
}

async function uploadAvatar(file) {
  const formData = new FormData();
  formData.append("file", file);
  try {
    const res = await fetch(`${API_BASE}/api/users/avatar`, {
      method: "POST",
      headers: { "Authorization": `Bearer ${token}`, "X-CSRF-Token": getCsrfToken() },
      credentials: "same-origin",
      body: formData
    });
    if (!res.ok) { alert("Avatar upload failed."); return; }
    const { avatarFileId } = await res.json();
    userAvatarFileId = avatarFileId;
    localStorage.setItem("chatAvatarFileId", avatarFileId);
    const preview = document.getElementById("profile-avatar-preview");
    renderAvatarInto(preview, userDisplayName || username, userAvatarColor, avatarFileId);
  } catch { alert("Avatar upload failed: network error."); }
}

async function saveProfile() {
  const displayName = document.getElementById("profile-display-name").value.trim();
  try {
    const res = await apiFetch("/api/users/me", "PATCH", {
      displayName: displayName || null,
      avatarColor: userAvatarColor
    });
    if (!res.ok) { alert("Failed to save profile."); return; }
    const data = await res.json();
    userDisplayName = data.displayName || displayName || username;
    userAvatarColor = data.avatarColor || userAvatarColor;
    userAvatarFileId = data.avatarFileId || userAvatarFileId;
    localStorage.setItem("chatDisplayName", userDisplayName);
    localStorage.setItem("chatAvatarColor", userAvatarColor);
    document.getElementById("current-user").textContent = userDisplayName;
    renderSelfAvatar();
    hideProfileModal();
  } catch { alert("Failed to save profile: network error."); }
}

// ── Rooms ─────────────────────────────────────────────────────────────────────
async function loadRooms() {
  try {
    const res = await apiFetch("/api/rooms");
    if (!res.ok) return;
    const rooms = await res.json();
    renderRooms(rooms);
    // Auto-open #random (or first available room) on initial load
    if (!activeRoom && rooms.length > 0) {
      const defaultRoom = rooms.find(r => r.name === "random") || rooms[0];
      switchRoom(defaultRoom);
    }
  } catch { /* silently fail */ }
}

function renderRooms(rooms) {
  const list = document.getElementById("room-list");
  list.innerHTML = "";
  if (rooms.length === 0) {
    list.innerHTML = '<li class="room-empty">No channels yet</li>';
    return;
  }
  rooms.forEach((room) => {
    const li = document.createElement("li");
    li.className = "room-item";
    li.dataset.id = room.id;
    li.setAttribute("role", "listitem");
    const lockIcon = room.isPrivate ? '<span class="room-lock" title="Private">🔒</span>' : '';
    li.innerHTML = `<span class="room-hash">#</span><span class="room-name">${escapeHtml(room.name)}</span>${lockIcon}`;
    li.addEventListener("click", () => switchRoom(room));
    list.appendChild(li);
  });
}

async function switchRoom(room) {
  if (activeRoom?.id === room.id) return;
  activeRoom = room;

  document.querySelectorAll(".room-item").forEach((el) =>
    el.classList.toggle("active", el.dataset.id === room.id));
  // Deselect any active DM when switching to a channel
  document.querySelectorAll(".dm-item").forEach(el => el.classList.remove("active"));

  // Clear unread badge for this room
  unreadCounts.delete(room.id);
  const activeRoomEl = document.querySelector(`.room-item[data-id="${CSS.escape(room.id)}"]`);
  if (activeRoomEl) { const dot = activeRoomEl.querySelector(".room-unread"); if (dot) dot.remove(); }

  document.getElementById("room-title").textContent = room.name;
  document.getElementById("room-description").textContent = room.description || "";
  document.getElementById("message-input").placeholder = `Message #${room.name}`;

  const privateBadge = document.getElementById("room-private-badge");
  const inviteBtn    = document.getElementById("invite-btn");
  if (room.isPrivate) {
    privateBadge.classList.remove("hidden");
    inviteBtn.classList.remove("hidden");
  } else {
    privateBadge.classList.add("hidden");
    inviteBtn.classList.add("hidden");
  }

  document.getElementById("no-room").classList.add("hidden");
  document.getElementById("chat-panel").classList.remove("hidden");
  document.getElementById("messages").innerHTML = "";
  document.getElementById("typing-indicator").classList.add("hidden");
  clearReply();

  closeSidebar();   // auto-close on mobile after selecting a room

  await loadHistory(room.id);

  if (connection?.state === signalR.HubConnectionState.Connected) {
    if (!joinedRooms.has(room.id)) {
      await connection.invoke("JoinRoom", room.id).catch(console.error);
      joinedRooms.add(room.id);
    }
    connection.invoke("SetActiveRoom", room.id).catch(console.error);
  }
}

// ── Message History ───────────────────────────────────────────────────────────
// Tracks the oldest message ID loaded per room (for pagination)
const oldestMsgId = new Map(); // roomId → msgId

async function loadHistory(roomId) {
  oldestMsgId.delete(roomId);
  removeLoadOlderBtn();
  try {
    const res = await apiFetch(`/api/rooms/${roomId}/messages`);
    if (!res.ok) return;
    const msgs = await res.json();
    msgs.forEach((m) => appendMessage(m));
    const container = document.getElementById("messages");
    container.scrollTop = container.scrollHeight;
    if (msgs.length > 0) {
      oldestMsgId.set(roomId, msgs[0].id);
      insertLoadOlderBtn(roomId);
    }
  } catch { /* history unavailable */ }
}

function removeLoadOlderBtn() {
  document.getElementById("load-older-btn")?.remove();
}

function insertLoadOlderBtn(roomId) {
  removeLoadOlderBtn();
  const container = document.getElementById("messages");
  const btn = document.createElement("button");
  btn.id = "load-older-btn";
  btn.className = "load-older-btn";
  btn.textContent = "Load older messages";
  btn.addEventListener("click", () => loadOlderMessages(roomId));
  container.prepend(btn);
}

async function loadOlderMessages(roomId) {
  const before = oldestMsgId.get(roomId);
  if (!before) return;

  const btn = document.getElementById("load-older-btn");
  if (btn) { btn.disabled = true; btn.textContent = "Loading…"; }

  try {
    const res = await apiFetch(`/api/rooms/${roomId}/messages?before=${encodeURIComponent(before)}`);
    if (!res.ok) return;
    const msgs = await res.json();

    const container = document.getElementById("messages");
    const prevHeight = container.scrollHeight;
    const prevScroll = container.scrollTop;

    // Remove the button before prepending so we can re-insert at the very top
    removeLoadOlderBtn();

    // Prepend messages in reverse so oldest appears at top
    for (let i = msgs.length - 1; i >= 0; i--) {
      prependMessage(msgs[i]);
    }

    // Restore scroll position so the user stays at the same spot
    container.scrollTop = prevScroll + (container.scrollHeight - prevHeight);

    if (msgs.length > 0) {
      oldestMsgId.set(roomId, msgs[0].id);
      insertLoadOlderBtn(roomId);
    }
    // If fewer messages were returned than expected, there's nothing older — don't re-add the button
  } catch {
    if (btn) { btn.disabled = false; btn.textContent = "Load older messages"; }
  }
}

// Prepend a single message at the top of the messages container
function prependMessage(msg) {
  const container = document.getElementById("messages");
  const isMine  = msg.senderId === userId;
  const time    = new Date(msg.sentAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  const displayName = msg.senderDisplayName || msg.senderUsername || "?";
  const initial = displayName[0].toUpperCase();
  const avatarColor = safeColor(msg.senderAvatarColor);

  // Group with the message that currently follows this one in time
  // (the element immediately after the load-older-btn, or the first child)
  const loadBtn = document.getElementById("load-older-btn");
  const nextEl  = loadBtn ? loadBtn.nextElementSibling : container.firstElementChild;
  const isGrouped = nextEl?.dataset?.author === msg.senderId
                    && !nextEl?.classList.contains("system");

  const div = document.createElement("div");
  div.className = `message${isMine ? " mine" : ""}${isGrouped ? " grouped" : ""}`;
  div.dataset.author = msg.senderId;
  div.dataset.msgId  = msg.id;
  if (msg.deleted) div.classList.add("deleted");

  const contentHtml = msg.deleted
    ? '<em class="msg-deleted">Message deleted</em>'
    : renderMarkdown(msg.content);
  const rawAttr = escapeHtml(msg.content || "");

  if (isGrouped) {
    div.innerHTML = `
      <div class="msg-avatar-spacer"></div>
      <div class="msg-content" data-raw="${rawAttr}">${contentHtml}</div>`;
  } else {
    div.innerHTML = `
      <div class="msg-avatar" style="background:${avatarColor}" title="${escapeHtml(displayName)}">${initial}</div>
      <div class="msg-body">
        <div class="msg-meta">
          <span class="msg-author">${escapeHtml(displayName)}</span>
          <span class="msg-time">${time}</span>
          ${msg.editedAt ? '<span class="msg-edited">(edited)</span>' : ""}
        </div>
        <div class="msg-content" data-raw="${rawAttr}">${contentHtml}</div>
      </div>`;
  }

  // Avatar image
  if (msg.senderAvatarFileId && !isGrouped) {
    const avatarEl = div.querySelector(".msg-avatar");
    if (avatarEl) {
      loadImageSrc(msg.senderAvatarFileId).then(src => {
        if (src) {
          avatarEl.style.background = "none";
          const img = document.createElement("img");
          img.src = src; img.alt = displayName;
          avatarEl.innerHTML = "";
          avatarEl.appendChild(img);
        }
      });
    }
  }

  // Attachments (images / voice messages)
  if (msg.attachmentId) {
    const target = div.querySelector(".msg-body .msg-content") || div.querySelector(".msg-content");
    if (target) {
      const baseType = (msg.attachmentType || "").split(";")[0].trim();
      if (baseType.startsWith("audio/")) {
        const audio = document.createElement("audio");
        audio.className = "msg-audio";
        audio.controls  = true;
        audio.preload   = "metadata";
        loadImageSrc(msg.attachmentId).then(src => { if (src) audio.src = src; });
        target.appendChild(audio);
      } else {
        const imgWrap = document.createElement("div");
        imgWrap.className = "msg-image-wrap";
        const img = document.createElement("img");
        img.className = "msg-image";
        img.alt = msg.attachmentName ?? "image";
        img.title = "Click to enlarge";
        img.setAttribute("loading", "lazy");
        loadImageSrc(msg.attachmentId).then(src => {
          if (src) {
            img.src = src;
            img.addEventListener("click", () => showLightbox(src));
          } else {
            imgWrap.textContent = `[Image: ${msg.attachmentName ?? "attachment"}]`;
          }
        });
        imgWrap.appendChild(img);
        target.appendChild(imgWrap);
      }
    }
  }

  // Reactions
  if (msg.reactions && Object.keys(msg.reactions).length > 0)
    renderReactions(div, msg.reactions, msg.id);

  // Link preview
  if (!msg.deleted && msg.content) {
    const urlMatch = msg.content.match(/https?:\/\/[^\s<>"]{8,}/);
    if (urlMatch) {
      const contentEl = div.querySelector(".msg-content");
      if (contentEl) fetchLinkPreview(urlMatch[0], contentEl);
    }
  }

  if (!msg.deleted) {
    div.addEventListener("contextmenu", (e) => { e.preventDefault(); showContextMenu(e, msg.id, msg.roomId, isMine); });
    const actBtn = document.createElement("button");
    actBtn.className = "msg-actions-btn";
    actBtn.innerHTML = "&#8942;";
    actBtn.setAttribute("aria-label", "Message options");
    actBtn.addEventListener("click", (e) => { e.stopPropagation(); showContextMenu(e, msg.id, msg.roomId, isMine); });
    div.appendChild(actBtn);
  }

  // Insert after the load-older-btn (which is the first child) or at the very beginning
  if (loadBtn) {
    loadBtn.insertAdjacentElement("afterend", div);
  } else {
    container.prepend(div);
  }
}

// ── Create Channel Modal ──────────────────────────────────────────────────────
function showCreateRoom() {
  document.getElementById("create-room-modal").classList.remove("hidden");
  setTimeout(() => document.getElementById("new-room-name").focus(), 50);
}

function hideCreateRoom() {
  document.getElementById("create-room-modal").classList.add("hidden");
  document.getElementById("new-room-name").value = "";
  document.getElementById("new-room-desc").value = "";
  document.getElementById("new-room-private").checked = false;
}

async function createRoom() {
  const rawName    = document.getElementById("new-room-name").value.trim();
  const name       = rawName.toLowerCase().replace(/\s+/g, "-").replace(/[^a-z0-9-]/g, "");
  const description= document.getElementById("new-room-desc").value.trim();
  const isPrivate  = document.getElementById("new-room-private").checked;
  if (!name) return;
  try {
    const res = await apiFetch("/api/rooms", "POST", { name, description, isPrivate });
    if (res.status === 409) { alert("A channel with that name already exists."); return; }
    if (!res.ok) return;
    const room = await res.json();
    hideCreateRoom();
    await loadRooms();
    await switchRoom(room);
  } catch { alert("Failed to create channel."); }
}

// ── Invite Modal ──────────────────────────────────────────────────────────────
async function showInviteModal() {
  if (!activeRoom?.isPrivate) return;
  document.getElementById("invite-room-name").textContent = activeRoom.name;
  try {
    const res = await apiFetch("/api/admin/users");
    const select = document.getElementById("invite-user-select");
    select.innerHTML = "";
    if (res.ok) {
      const users = await res.json();
      const memberSet = new Set(activeRoom.memberIds || []);
      users.filter(u => !memberSet.has(u.id)).forEach(u => {
        const opt = document.createElement("option");
        opt.value = u.id;
        opt.textContent = u.username;
        select.appendChild(opt);
      });
    }
  } catch { /* ignore */ }
  document.getElementById("invite-modal").classList.remove("hidden");
}

function hideInviteModal() {
  document.getElementById("invite-modal").classList.add("hidden");
}

async function inviteUser() {
  const select = document.getElementById("invite-user-select");
  const targetUserId = select.value;
  if (!targetUserId || !activeRoom) return;
  try {
    const res = await apiFetch(`/api/rooms/${activeRoom.id}/invite`, "POST", { userId: targetUserId });
    if (!res.ok) { alert("Failed to invite user."); return; }
    if (!activeRoom.memberIds) activeRoom.memberIds = [];
    activeRoom.memberIds.push(targetUserId);
    appendSystemMessage(`User invited to #${activeRoom.name}.`);
    hideInviteModal();
  } catch { alert("Invite failed: network error."); }
}

// ── SignalR Connection ────────────────────────────────────────────────────────
async function connectSignalR() {
  connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE}/chathub`, { accessTokenFactory: () => token })
    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
    .configureLogging(signalR.LogLevel.Warning)
    .build();

  // Server-side rate limiting feedback
  connection.on("RateLimited", (reason) => {
    appendSystemMessage(reason || "Slow down — you're doing that too fast.");
  });

  // Admin force-logout (account disabled while connected)
  connection.on("ForceLogout", (reason) => {
    alert(reason || "You have been logged out.");
    logout();
  });

  connection.on("ReceiveMessage", (msg) => {
    if (msg.roomId === activeRoom?.id) {
      // Replace optimistic placeholder if the server is confirming our own message
      if (msg.clientMsgId) {
        const placeholder = document.querySelector(`[data-msg-id="${CSS.escape(msg.clientMsgId)}"]`);
        if (placeholder) {
          placeholder.dataset.msgId = msg.id;
          delete placeholder.dataset._optimistic;
          return; // placeholder already visible — no need to append again
        }
      }
      appendMessage(msg, true);
    } else if (msg.roomId.startsWith("dm_")) {
      // Message in a DM we're not currently viewing — add to sidebar and show unread dot
      const parts    = msg.roomId.split("_");
      const otherUid = parts[1] === userId ? parts[2] : parts[1];
      const otherUname = onlineUsers.get(otherUid) || msg.senderUsername || "User";
      addDmConversation(otherUid, otherUname);
      unreadCounts.set(msg.roomId, (unreadCounts.get(msg.roomId) || 0) + 1);
      markDmUnread(msg.roomId);
    } else {
      // Message in a channel we're not currently viewing — show unread dot
      unreadCounts.set(msg.roomId, (unreadCounts.get(msg.roomId) || 0) + 1);
      markRoomUnread(msg.roomId);
    }
  });

  connection.on("OnlineUsers", (users) => {
    onlineUsers.clear();
    users.forEach(u => onlineUsers.set(u.userId, u.username));
    renderOnlineList();
  });

  connection.on("UserOnline", (uid, uname) => {
    onlineUsers.set(uid, uname);
    renderOnlineList();
  });

  connection.on("UserOffline", (uid) => {
    onlineUsers.delete(uid);
    renderOnlineList();
  });

  connection.on("UserTyping", (uname, roomId) => {
    if (roomId !== activeRoom?.id || uname === username) return;
    showTyping(uname);
  });

  connection.on("MessageEdited", ({ messageId, content, editedAt }) => {
    const div = document.querySelector(`[data-msg-id="${messageId}"]`);
    if (!div) return;
    const contentEl = div.querySelector(".msg-content");
    if (contentEl) {
      contentEl.innerHTML = renderMarkdown(content);
      contentEl.dataset.raw = content;
    }
    let editedMark = div.querySelector(".msg-edited");
    if (!editedMark) {
      editedMark = document.createElement("span");
      editedMark.className = "msg-edited";
      editedMark.textContent = " (edited)";
      div.querySelector(".msg-meta")?.appendChild(editedMark);
    }
  });

  connection.on("MessageDeleted", ({ messageId }) => {
    const div = document.querySelector(`[data-msg-id="${messageId}"]`);
    if (!div) return;
    const contentEl = div.querySelector(".msg-content");
    if (contentEl) contentEl.innerHTML = '<em class="msg-deleted">Message deleted</em>';
    div.classList.add("deleted");
  });

  connection.on("ReactionsUpdated", ({ messageId, reactions }) => {
    const div = document.querySelector(`[data-msg-id="${messageId}"]`);
    if (div) renderReactions(div, reactions, messageId);
  });

  connection.on("UserJoined", (uname, roomId) => {
    if (roomId.startsWith("dm_")) return;  // suppress for DMs
    if (roomId === activeRoom?.id && uname !== username)
      appendSystemMessage(`${uname} joined #${activeRoom.name}`);
  });

  connection.on("UserLeft", (uname, roomId) => {
    if (roomId.startsWith("dm_")) return;
    if (roomId === activeRoom?.id)
      appendSystemMessage(`${uname} left #${activeRoom.name}`);
  });

  // DM invite: recipient is told to silently join the DM group
  connection.on("DmInvite", (dmRoomId) => {
    if (connection?.state === signalR.HubConnectionState.Connected
        && !joinedRooms.has(dmRoomId)) {
      joinedRooms.add(dmRoomId);
      connection.invoke("JoinRoom", dmRoomId).catch(() => {});
    }
    // Add the DM conversation to the sidebar (best-effort: look up from online users)
    const parts    = dmRoomId.split("_");   // dm_{uid1}_{uid2}
    const otherUid = parts[1] === userId ? parts[2] : parts[1];
    const otherUname = onlineUsers.get(otherUid) || "User";
    addDmConversation(otherUid, otherUname);
    markDmUnread(dmRoomId);
  });

  connection.onreconnecting(() => setConnectionStatus(false));
  connection.onreconnected(async () => {
    setConnectionStatus(true);
    for (const id of joinedRooms)
      await connection.invoke("JoinRoom", id).catch(console.error);
    // Re-join DM rooms from persisted history that aren't already tracked
    dmConversations.forEach(({ roomId }) => {
      if (!joinedRooms.has(roomId)) {
        connection.invoke("JoinRoom", roomId).catch(() => {});
        joinedRooms.add(roomId);
      }
    });
    // Re-announce which room we're viewing
    if (activeRoom?.id)
      connection.invoke("SetActiveRoom", activeRoom.id).catch(console.error);
    // Flush queued messages
    while (sendQueue.length > 0) {
      const { roomId, content } = sendQueue.shift();
      await connection.invoke("SendMessage", roomId, content).catch(console.error);
    }
  });
  connection.onclose((err) => {
    setConnectionStatus(false);
    // If we reach onclose after automatic retries are exhausted, show a
    // persistent banner so the user knows they're offline and can retry.
    if (err) console.error("SignalR connection closed with error:", err);
    showReconnectBanner();
  });

  try {
    await connection.start();
    setConnectionStatus(true);
    // Tell the hub which room we're currently viewing (e.g. after page reload)
    if (activeRoom?.id)
      connection.invoke("SetActiveRoom", activeRoom.id).catch(console.error);
  } catch (err) {
    console.error("SignalR failed to connect:", err);
    setConnectionStatus(false);
  }
}

function setConnectionStatus(online) {
  const el = document.getElementById("connection-status");
  if (!el) return;
  el.textContent = online ? "● Live" : "● Reconnecting…";
  el.className = `status-badge ${online ? "connected" : "disconnected"}`;
  // Hide the reconnect banner when we come back online
  if (online) hideReconnectBanner();
}

function showReconnectBanner() {
  if (document.getElementById("reconnect-banner")) return; // already showing
  const banner = document.createElement("div");
  banner.id = "reconnect-banner";
  banner.setAttribute("role", "alert");
  banner.innerHTML = `
    <span>Connection lost. Messages may be missed.</span>
    <button onclick="manualReconnect()" class="btn-reconnect">Reconnect</button>`;
  document.body.appendChild(banner);
}

function hideReconnectBanner() {
  document.getElementById("reconnect-banner")?.remove();
}

async function manualReconnect() {
  const btn = document.querySelector(".btn-reconnect");
  if (btn) { btn.textContent = "Connecting…"; btn.disabled = true; }
  try {
    await connectSignalR();
    hideReconnectBanner();
    // Reload current room's history to catch messages we missed
    if (activeRoom?.id) await loadHistory(activeRoom.id);
  } catch {
    if (btn) { btn.textContent = "Retry"; btn.disabled = false; }
  }
}

// ── Online Users Sidebar ──────────────────────────────────────────────────────
function renderOnlineList() {
  const list  = document.getElementById("online-list");
  const count = document.getElementById("online-count");
  if (!list) return;
  list.innerHTML = "";
  count.textContent = onlineUsers.size;
  onlineUsers.forEach((uname, uid) => {
    const li = document.createElement("li");
    li.className = "online-item";
    const isSelf = uid === userId;
    li.innerHTML = `
      <div class="presence-dot online"></div>
      <span class="online-name">${escapeHtml(uname)}</span>
      ${!isSelf ? `<button class="btn-dm" onclick="openDm('${escapeHtml(uid)}','${escapeHtml(uname)}')" title="Direct message">DM</button>` : ''}`;
    list.appendChild(li);
  });
}

// ── Direct Messages ───────────────────────────────────────────────────────────
async function openDm(otherUserId, otherUsername) {
  // Derive a consistent DM room ID from the two participants' IDs (alphabetical)
  const sorted    = [userId, otherUserId].sort();
  const dmRoomId  = `dm_${sorted[0]}_${sorted[1]}`;

  // Avoid reloading if we're already in this DM
  if (activeRoom?.id === dmRoomId) { closeSidebar(); return; }

  // Add to DM sidebar list and persist
  addDmConversation(otherUserId, otherUsername);

  activeRoom = { id: dmRoomId, name: otherUsername, isDm: true, dmRecipientId: otherUserId };

  // Update header: use “@” marker instead of “#”
  document.getElementById("room-hash").textContent = "@";
  document.getElementById("room-title").textContent = otherUsername;
  document.getElementById("room-description").textContent = "Direct Message";
  document.getElementById("room-private-badge").classList.add("hidden");
  document.getElementById("invite-btn").classList.add("hidden");
  document.getElementById("message-input").placeholder = `Message @${otherUsername}`;

  // Deselect any active channel in the room list; mark active DM
  document.querySelectorAll(".room-item").forEach(el => el.classList.remove("active"));
  document.querySelectorAll(".dm-item").forEach(el => el.classList.remove("active"));
  document.querySelector(`.dm-item[data-uid="${CSS.escape(otherUserId)}"]`)?.classList.add("active");
  // Clear unread badge for this DM
  unreadCounts.delete(dmRoomId);
  const dmItemEl = document.querySelector(`.dm-item[data-uid="${CSS.escape(otherUserId)}"]`);
  if (dmItemEl) { const badge = dmItemEl.querySelector(".dm-unread"); if (badge) badge.remove(); }

  document.getElementById("no-room").classList.add("hidden");
  document.getElementById("chat-panel").classList.remove("hidden");
  document.getElementById("messages").innerHTML = "";
  document.getElementById("typing-indicator").classList.add("hidden");
  clearReply();

  closeSidebar();

  // Tell the hub to set up the DM group (puts both parties in it)
  if (connection?.state === signalR.HubConnectionState.Connected) {
    if (!joinedRooms.has(dmRoomId)) {
      await connection.invoke("JoinDm", otherUserId).catch(console.error);
      joinedRooms.add(dmRoomId);
    }
    connection.invoke("SetActiveRoom", dmRoomId).catch(console.error);
  }

  await loadHistory(dmRoomId);
}

// ── New DM Modal ──────────────────────────────────────────────────────────────
let allUsersCache = [];

async function showNewDmModal() {
  document.getElementById("new-dm-modal").classList.remove("hidden");
  document.getElementById("new-dm-search").value = "";
  document.getElementById("new-dm-results").innerHTML = "";
  setTimeout(() => document.getElementById("new-dm-search").focus(), 50);
  // Fetch user list if not cached
  if (allUsersCache.length === 0) {
    try {
      const res = await apiFetch("/api/users");
      if (res.ok) allUsersCache = await res.json();
    } catch { /* ignore */ }
  }
  renderNewDmResults(allUsersCache);
}

function hideNewDmModal() {
  document.getElementById("new-dm-modal").classList.add("hidden");
}

function onNewDmSearch(query) {
  const q = query.toLowerCase().trim();
  const filtered = q
    ? allUsersCache.filter(u => u.username.toLowerCase().includes(q) || u.displayName.toLowerCase().includes(q))
    : allUsersCache;
  renderNewDmResults(filtered);
}

function renderNewDmResults(users) {
  const ul = document.getElementById("new-dm-results");
  ul.innerHTML = "";
  users.forEach(u => {
    if (u.id === userId) return; // don't DM yourself
    const li = document.createElement("li");
    li.className = "dm-search-item";
    li.innerHTML = `<span class="dm-search-name">${escapeHtml(u.displayName || u.username)}</span>
                    <span class="dm-search-username">@${escapeHtml(u.username)}</span>`;
    li.addEventListener("click", () => {
      hideNewDmModal();
      openDm(u.id, u.displayName || u.username);
    });
    ul.appendChild(li);
  });
  if (users.filter(u => u.id !== userId).length === 0) {
    ul.innerHTML = "<li class='dm-search-empty'>No users found</li>";
  }
}

// ── DM Sidebar Helpers ────────────────────────────────────────────────────────
async function loadDmConversations() {
  // 1. Seed from localStorage for an instant render
  try {
    const stored = JSON.parse(localStorage.getItem("chatDmList") || "[]");
    dmConversations.clear();
    stored.forEach(([uid, info]) => dmConversations.set(uid, info));
  } catch { /* ignore malformed data */ }

  // 2. Sync from server so history survives localStorage being cleared
  try {
    const res = await apiFetch("/api/users/dm-conversations");
    if (res.ok) {
      const convs = await res.json();
      convs.forEach(({ uid, username, roomId }) => {
        if (!dmConversations.has(uid))
          dmConversations.set(uid, { username, roomId });
      });
      saveDmConversations();
    }
  } catch { /* server unavailable — keep local cache */ }

  renderDmList();

  // 3. Silently re-join each DM SignalR group so real-time messages arrive
  //    without needing to open the conversation first.
  if (connection?.state === signalR.HubConnectionState.Connected) {
    dmConversations.forEach(({ roomId }) => {
      if (!joinedRooms.has(roomId)) {
        connection.invoke("JoinRoom", roomId).catch(() => {});
        joinedRooms.add(roomId);
      }
    });
  }
}

function saveDmConversations() {
  localStorage.setItem("chatDmList",
    JSON.stringify(Array.from(dmConversations.entries())));
}

function renderDmList() {
  const list   = document.getElementById("dm-list");
  const emptyEl = document.getElementById("dm-list-empty");
  if (!list) return;
  // Remove existing dm-item elements (keep the empty placeholder)
  list.querySelectorAll(".dm-item").forEach(el => el.remove());
  if (dmConversations.size === 0) {
    if (!emptyEl) {
      const li = document.createElement("li");
      li.id = "dm-list-empty";
      li.className = "room-empty";
      li.textContent = "No conversations yet";
      list.appendChild(li);
    }
    return;
  }
  if (emptyEl) emptyEl.remove();
  dmConversations.forEach(({ username: uname }, uid) => {
    const li = document.createElement("li");
    li.className = "dm-item";
    li.dataset.uid = uid;
    const isActive = activeRoom?.isDm && activeRoom?.dmRecipientId === uid;
    if (isActive) li.classList.add("active");
    li.innerHTML = `
      <div class="dm-avatar-sm" title="${escapeHtml(uname)}">${escapeHtml(uname[0].toUpperCase())}</div>
      <span class="dm-name">${escapeHtml(uname)}</span>`;
    li.addEventListener("click", () => openDm(uid, uname));
    list.appendChild(li);
  });
}

function addDmConversation(uid, uname) {
  if (!uid || !uname) return;
  if (!dmConversations.has(uid)) {
    dmConversations.set(uid, { username: uname, roomId: `dm_${[userId, uid].sort().join("_")}` });
    saveDmConversations();
  }
  renderDmList();
}

function markDmUnread(roomId) {
  let targetUid = null;
  dmConversations.forEach(({ roomId: r }, uid) => { if (r === roomId) targetUid = uid; });
  if (!targetUid) return;
  const item = document.querySelector(`.dm-item[data-uid="${CSS.escape(targetUid)}"]`);
  if (!item) return;
  const count = unreadCounts.get(roomId) || 0;
  let badge = item.querySelector(".dm-unread");
  if (!badge) {
    badge = document.createElement("span");
    badge.className = "dm-unread";
    item.appendChild(badge);
  }
  badge.textContent = count > 0 ? String(count > 99 ? "99+" : count) : "●";
}

function markRoomUnread(roomId) {
  const item = document.querySelector(`.room-item[data-id="${CSS.escape(roomId)}"]`);
  if (!item || item.classList.contains("active")) return;
  const count = unreadCounts.get(roomId) || 0;
  let dot = item.querySelector(".room-unread");
  if (!dot) {
    dot = document.createElement("span");
    dot.className = "room-unread";
    item.appendChild(dot);
  }
  if (count > 0) {
    dot.textContent = String(count > 99 ? "99+" : count);
    dot.classList.add("has-count");
  } else {
    dot.textContent = "";
    dot.classList.remove("has-count");
  }
}
let typingThrottle = 0;

function broadcastTyping() {
  if (!activeRoom || !connection || connection.state !== signalR.HubConnectionState.Connected) return;
  const now = Date.now();
  if (now - typingThrottle < 2000) return;
  typingThrottle = now;
  connection.invoke("SendTyping", activeRoom.id).catch(() => {});
}

function showTyping(uname) {
  const el = document.getElementById("typing-indicator");
  if (!el) return;
  clearTimeout(typingTimers[uname]);
  el.textContent = `${escapeHtml(uname)} is typing…`;
  el.classList.remove("hidden");
  typingTimers[uname] = setTimeout(() => { el.classList.add("hidden"); }, 3000);
}

// ── Send Message ──────────────────────────────────────────────────────────────
async function sendMessage() {
  const input = document.getElementById("message-input");
  let content = input.value.trim();
  if (!content || !activeRoom || !connection) return;

  // Prepend reply quote if active
  if (replyToMessage) {
    const quoteLine = `> **@${replyToMessage.senderDisplayName}:** ${replyToMessage.content.slice(0, 80)}${replyToMessage.content.length > 80 ? "…" : ""}`;
    content = quoteLine + "\n" + content;
    clearReply();
  }

  if (connection.state !== signalR.HubConnectionState.Connected) {
    sendQueue.push({ roomId: activeRoom.id, content });
    input.value = "";
    appendSystemMessage("Queued — will send when reconnected.");
    return;
  }

  // Optimistic UI: show message immediately; server will confirm with clientMsgId
  const clientMsgId = `opt_${Date.now()}_${Math.random().toString(36).slice(2)}`;
  const optimisticMsg = {
    id: clientMsgId,
    clientMsgId,
    roomId: activeRoom.id,
    senderId: userId,
    senderUsername: username,
    senderDisplayName: userDisplayName || username,
    senderAvatarColor: userAvatarColor,
    senderAvatarFileId: userAvatarFileId,
    content,
    deleted: false,
    editedAt: null,
    reactions: {},
    sentAt: new Date().toISOString(),
    _optimistic: true
  };
  appendMessage(optimisticMsg, true);

  input.value = "";
  // On mobile, dismiss the virtual keyboard after sending so the user can see the chat
  if ("ontouchstart" in window) input.blur();

  try {
    await connection.invoke("SendMessage", activeRoom.id, content, clientMsgId);
  } catch (err) {
    console.error("Send failed:", err);
    // Remove the optimistic placeholder on failure
    document.querySelector(`[data-msg-id="${CSS.escape(clientMsgId)}"]`)?.remove();
    input.value = content;
  }
}

// ── Voice Recording ───────────────────────────────────────────────────────────
function toggleRecording() {
  if (isRecording) stopRecording();
  else startRecording();
}

async function startRecording() {
  if (!activeRoom) { appendSystemMessage("Join a channel first."); return; }
  if (!navigator.mediaDevices?.getUserMedia) {
    appendSystemMessage("Microphone not available — voice messages require a secure (HTTPS) connection.");
    return;
  }

  // Check mic permission state and show an explanation if not yet granted
  try {
    const permStatus = await navigator.permissions?.query({ name: "microphone" }).catch(() => null);
    if (permStatus && permStatus.state === "prompt") {
      appendSystemMessage("🎙️ ChatApp needs microphone access to record voice messages. Please allow it when prompted.");
    } else if (permStatus && permStatus.state === "denied") {
      appendSystemMessage("🎙️ Microphone access is blocked. Please enable it in your browser's site settings to record voice messages.");
      return;
    }
  } catch { /* permissions API not available — fall through to getUserMedia */ }

  try {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    // Pick the best supported format
    const formats = [
      "audio/webm;codecs=opus", "audio/webm",
      "audio/ogg;codecs=opus",  "audio/ogg",
      "audio/mp4"
    ];
    const mimeType = formats.find(f => MediaRecorder.isTypeSupported(f)) || "";
    mediaRecorder = new MediaRecorder(stream, mimeType ? { mimeType } : {});
    audioChunks   = [];
    mediaRecorder.addEventListener("dataavailable", e => { if (e.data.size) audioChunks.push(e.data); });
    mediaRecorder.addEventListener("stop", () => {
      stream.getTracks().forEach(t => t.stop());
      const blob   = new Blob(audioChunks, { type: mediaRecorder.mimeType || "audio/webm" });
      uploadVoiceMessage(blob);
    });
    mediaRecorder.start();
    isRecording = true;
    const btn = document.getElementById("mic-btn");
    if (btn) {
      btn.classList.add("recording");
      btn.title = "Stop recording";
      // Show elapsed time in the button
      let secs = 0;
      recTimerRef = setInterval(() => {
        secs++;
        btn.title = `Recording ${secs}s — click to send`;
      }, 1000);
    }
  } catch (err) {
    appendSystemMessage("Microphone access denied or unavailable.");
    console.error(err);
  }
}

function stopRecording() {
  if (!mediaRecorder || mediaRecorder.state === "inactive") return;
  mediaRecorder.stop();
  isRecording = false;
  clearInterval(recTimerRef);
  const btn = document.getElementById("mic-btn");
  if (btn) { btn.classList.remove("recording"); btn.title = "Record voice message"; }
}

async function uploadVoiceMessage(blob) {
  if (!activeRoom) return;
  if (blob.size > 10 * 1024 * 1024) { appendSystemMessage("Recording too large (10 MB max)."); return; }
  appendSystemMessage("Sending voice message…");
  const ext      = blob.type.includes("ogg") ? "ogg" : blob.type.includes("mp4") ? "m4a" : "webm";
  const file     = new File([blob], `voice-${Date.now()}.${ext}`, { type: blob.type });
  const formData = new FormData();
  formData.append("file", file);
  try {
    const headers = { "X-CSRF-Token": getCsrfToken() };
    if (token) headers["Authorization"] = `Bearer ${token}`;
    const res = await fetch(`${API_BASE}/api/rooms/${activeRoom.id}/upload`, {
      method: "POST",
      headers,
      credentials: "same-origin",
      body: formData
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      appendSystemMessage(`Upload failed: ${escapeHtml(err.error ?? "Unknown error")}`);
    }
  } catch { appendSystemMessage("Upload failed: Network error."); }
}

// ── Context Menu (right-click my messages) ────────────────────────────────────
function showContextMenu(e, msgId, roomId, isMyMessage) {
  e.preventDefault();
  ctxMessageId = msgId;
  ctxRoomId = roomId;
  const menu = document.getElementById("context-menu");

  // Show/hide edit+delete only for own messages
  const editBtn   = document.getElementById("ctx-edit-btn");
  const deleteBtn = document.getElementById("ctx-delete-btn");
  if (editBtn)   editBtn.style.display   = isMyMessage ? "" : "none";
  if (deleteBtn) deleteBtn.style.display = isMyMessage ? "" : "none";

  menu.style.left = `${Math.min(e.clientX, window.innerWidth - 160)}px`;
  menu.style.top  = `${Math.min(e.clientY, window.innerHeight - 100)}px`;
  menu.classList.remove("hidden");
}

function hideContextMenu() {
  document.getElementById("context-menu")?.classList.add("hidden");
  ctxMessageId = null;
  ctxRoomId = null;
}

function contextEdit() {
  if (!ctxMessageId) return;
  const div = document.querySelector(`[data-msg-id="${ctxMessageId}"]`);
  const contentEl = div?.querySelector(".msg-content");
  const currentContent = contentEl?.dataset?.raw ?? contentEl?.textContent?.trim() ?? "";
  hideContextMenu();
  showEditModal(ctxMessageId, activeRoom?.id, currentContent);
}

function contextDelete() {
  if (!ctxMessageId || !ctxRoomId) return;
  const msgId = ctxMessageId, roomId = ctxRoomId;
  hideContextMenu();
  if (!confirm("Delete this message?")) return;
  connection.invoke("DeleteMessage", roomId, msgId).catch(console.error);
}

function contextReply() {
  if (!ctxMessageId) return;
  const div = document.querySelector(`[data-msg-id="${ctxMessageId}"]`);
  const senderName = div?.querySelector(".msg-author")?.textContent?.trim() || "User";
  const content    = div?.querySelector(".msg-content")?.textContent?.trim() || "";
  hideContextMenu();
  setReplyTo(ctxMessageId, senderName, content);
}

// ── Edit Modal ────────────────────────────────────────────────────────────────
function showEditModal(msgId, roomId, currentContent) {
  editMessageId = msgId;
  editRoomId    = roomId;
  const input = document.getElementById("edit-content");
  input.value = currentContent;
  document.getElementById("edit-modal").classList.remove("hidden");
  setTimeout(() => { input.focus(); input.select(); }, 50);
}

function hideEditModal() {
  editMessageId = null;
  editRoomId    = null;
  document.getElementById("edit-modal").classList.add("hidden");
}

async function submitEdit() {
  const content = document.getElementById("edit-content").value.trim();
  if (!content || !editMessageId || !editRoomId) return;
  try {
    await connection.invoke("EditMessage", editRoomId, editMessageId, content);
    hideEditModal();
  } catch (err) { console.error("Edit failed:", err); }
}

// ── Reactions ─────────────────────────────────────────────────────────────────
function renderReactions(div, reactions, messageId) {
  let row = div.querySelector(".reactions-row");
  if (!row) {
    row = document.createElement("div");
    row.className = "reactions-row";
    (div.querySelector(".msg-body") || div.querySelector(".msg-content"))?.appendChild(row);
  }
  row.innerHTML = "";
  if (!reactions) return;
  Object.entries(reactions).forEach(([emoji, users]) => {
    if (!users || users.length === 0) return;
    const btn = document.createElement("button");
    btn.className = `reaction-btn${users.includes(userId) ? " reacted" : ""}`;
    btn.type = "button";
    btn.title = users.join(", ");
    btn.textContent = `${emoji} ${users.length}`;
    btn.addEventListener("click", () => {
      connection.invoke("ToggleReaction", activeRoom.id, messageId, emoji).catch(console.error);
    });
    row.appendChild(btn);
  });
  const addBtn = document.createElement("button");
  addBtn.className = "reaction-add";
  addBtn.type = "button";
  addBtn.title = "Add reaction";
  addBtn.textContent = "😊+";
  addBtn.addEventListener("click", (e) => { e.stopPropagation(); showReactionPicker(e, messageId); });
  row.appendChild(addBtn);
}

function showReactionPicker(e, messageId) {
  const emojis = ["👍","❤️","😂","🎉","🔥","😮","😢","👏","🤔","💯"];
  document.querySelectorAll(".emoji-picker").forEach(p => p.remove());
  const picker = document.createElement("div");
  picker.className = "emoji-picker";
  picker.style.left = `${Math.min(e.clientX, window.innerWidth - 220)}px`;
  picker.style.top  = `${Math.min(e.clientY, window.innerHeight - 60)}px`;
  emojis.forEach(em => {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.textContent = em;
    btn.addEventListener("click", () => {
      connection.invoke("ToggleReaction", activeRoom.id, messageId, em).catch(console.error);
      picker.remove();
    });
    picker.appendChild(btn);
  });
  document.body.appendChild(picker);
  setTimeout(() => document.addEventListener("click", () => picker.remove(), { once: true }), 0);
}

// ── Image Upload ───────────────────────────────────────────────────────────────
async function uploadFile(file) {
  if (!activeRoom) { appendSystemMessage("Join a channel first."); return; }
  if (file.size > 10 * 1024 * 1024) { appendSystemMessage("File must be under 10 MB."); return; }
  appendSystemMessage(`Uploading ${escapeHtml(file.name)}…`);
  const formData = new FormData();
  formData.append("file", file);
  try {
    const headers = { "X-CSRF-Token": getCsrfToken() };
    if (token) headers["Authorization"] = `Bearer ${token}`;
    const res = await fetch(`${API_BASE}/api/rooms/${activeRoom.id}/upload`, {
      method: "POST",
      headers,
      credentials: "same-origin",
      body: formData
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      appendSystemMessage(`Upload failed: ${escapeHtml(err.error ?? "Unknown error")}`);
    }
  } catch { appendSystemMessage("Upload failed: Network error."); }
}

// ── Authenticated image/audio blob loader (LRU-capped to prevent memory leaks) ─
const _blobUrlCache = new Map(); // fileId → objectURL
const BLOB_CACHE_MAX = 200;     // max cached blob URLs before eviction

function evictBlobCache() {
  while (_blobUrlCache.size > BLOB_CACHE_MAX) {
    const oldest = _blobUrlCache.keys().next().value;
    const url = _blobUrlCache.get(oldest);
    if (url) URL.revokeObjectURL(url);  // free browser memory
    _blobUrlCache.delete(oldest);
  }
}

async function loadImageSrc(fileId) {
  if (_blobUrlCache.has(fileId)) {
    // Move to end for LRU ordering
    const url = _blobUrlCache.get(fileId);
    _blobUrlCache.delete(fileId);
    _blobUrlCache.set(fileId, url);
    return url;
  }
  try {
    const res = await apiFetch(`/api/files/${fileId}`);
    if (!res.ok) return null;
    const url = URL.createObjectURL(await res.blob());
    _blobUrlCache.set(fileId, url);
    evictBlobCache();
    return url;
  } catch { return null; }
}

// ── Lightbox ──────────────────────────────────────────────────────────────────
function showLightbox(src) {
  const lb = document.getElementById("lightbox");
  document.getElementById("lightbox-img").src = src;
  lb.classList.remove("hidden");
}

function hideLightbox() {
  document.getElementById("lightbox").classList.add("hidden");
  document.getElementById("lightbox-img").src = "";
}

// ── Render Helpers ────────────────────────────────────────────────────────────
function appendMessage(msg, scrollIntoView = false) {
  const container = document.getElementById("messages");
  const isMine  = msg.senderId === userId;
  const time    = new Date(msg.sentAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  const displayName = msg.senderDisplayName || msg.senderUsername || "?";
  const initial = displayName[0].toUpperCase();
  const avatarColor = safeColor(msg.senderAvatarColor);

  const lastMsg   = container.lastElementChild;
  const isGrouped = lastMsg?.dataset?.author === msg.senderId && !lastMsg?.classList.contains("system");

  const div = document.createElement("div");
  div.className = `message${isMine ? " mine" : ""}${isGrouped ? " grouped" : ""}`;
  div.dataset.author = msg.senderId;
  div.dataset.msgId  = msg.id;
  if (msg.deleted) div.classList.add("deleted");

  const contentHtml = msg.deleted
    ? '<em class="msg-deleted">Message deleted</em>'
    : renderMarkdown(msg.content);

  if (isGrouped) {
    div.innerHTML = `
      <div class="msg-avatar-spacer"></div>
      <div class="msg-content" data-raw="${escapeHtml(msg.content||'')}">${contentHtml}</div>`;
  } else {
    div.innerHTML = `
      <div class="msg-avatar" style="background:${avatarColor}" title="${escapeHtml(displayName)}">${initial}</div>
      <div class="msg-body">
        <div class="msg-meta">
          <span class="msg-author">${escapeHtml(displayName)}</span>
          <span class="msg-time">${time}</span>
          ${msg.editedAt ? '<span class="msg-edited">(edited)</span>' : ""}
        </div>
        <div class="msg-content" data-raw="${escapeHtml(msg.content||'')}">${contentHtml}</div>
      </div>`;
  }

  if (msg.senderAvatarFileId && !isGrouped) {
    const avatarEl = div.querySelector(".msg-avatar");
    if (avatarEl) {
      loadImageSrc(msg.senderAvatarFileId).then(src => {
        if (src) {
          avatarEl.style.background = "none";
          const img = document.createElement("img");
          img.src = src; img.alt = displayName;
          avatarEl.innerHTML = "";
          avatarEl.appendChild(img);
        }
      });
    }
  }

  if (msg.attachmentId) {
    const target = div.querySelector(".msg-body .msg-content") || div.querySelector(".msg-content");
    if (target) {
      const baseType = (msg.attachmentType || "").split(";")[0].trim();
      if (baseType.startsWith("audio/")) {
        const audio = document.createElement("audio");
        audio.className = "msg-audio";
        audio.controls  = true;
        audio.preload   = "metadata";
        loadImageSrc(msg.attachmentId).then(src => { if (src) audio.src = src; });
        target.appendChild(audio);
      } else {
        const imgWrap = document.createElement("div");
        imgWrap.className = "msg-image-wrap";
        const img = document.createElement("img");
        img.className = "msg-image";
        img.alt = msg.attachmentName ?? "image";
        img.title = "Click to enlarge";
        img.setAttribute("loading", "lazy");
        loadImageSrc(msg.attachmentId).then(src => {
          if (src) {
            img.src = src;
            img.addEventListener("click", () => showLightbox(src));
          } else {
            imgWrap.textContent = `[Image: ${msg.attachmentName ?? "attachment"}]`;
          }
        });
        imgWrap.appendChild(img);
        target.appendChild(imgWrap);
      }
    }
  }

  if (msg.reactions && Object.keys(msg.reactions).length > 0)
    renderReactions(div, msg.reactions, msg.id);

  // Link preview: detect first URL in non-deleted, non-optimistic messages
  if (!msg.deleted && !msg._optimistic && msg.content) {
    const urlMatch = msg.content.match(/https?:\/\/[^\s<>"]{8,}/);
    if (urlMatch) {
      const contentEl = div.querySelector(".msg-content");
      if (contentEl) fetchLinkPreview(urlMatch[0], contentEl);
    }
  }

  if (!msg.deleted) {
    div.addEventListener("contextmenu", (e) => showContextMenu(e, msg.id, msg.roomId, isMine));
    const actBtn = document.createElement("button");
    actBtn.className = "msg-actions-btn";
    actBtn.innerHTML = "⋮";
    actBtn.setAttribute("aria-label", "Message options");
    actBtn.addEventListener("click", (e) => { e.stopPropagation(); showContextMenu(e, msg.id, msg.roomId, isMine); });
    div.appendChild(actBtn);
  }

  // Capture scroll distance BEFORE appending so the check isn't skewed by the new element's own height
  const distFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight;

  container.appendChild(div);

  // Smart scroll: always scroll if we were near the bottom; otherwise increment unread counter
  if (scrollIntoView) {
    if (distFromBottom < 150) {
      container.scrollTop = container.scrollHeight;
    } else {
      scrollUnreadCount++;
      onMessagesScroll();
    }
  }
}

// ── Scroll-to-bottom button ───────────────────────────────────────────────────
function onMessagesScroll() {
  const container = document.getElementById("messages");
  const btn       = document.getElementById("scroll-bottom-btn");
  if (!container || !btn) return;
  const distFromBottom = container.scrollHeight - container.scrollTop - container.clientHeight;
  if (distFromBottom > 120) {
    btn.classList.remove("hidden");
    if (scrollUnreadCount > 0) btn.textContent = `↓ ${scrollUnreadCount}`;
    else btn.textContent = "↓";
  } else {
    btn.classList.add("hidden");
    scrollUnreadCount = 0;
  }
}

function scrollToBottom() {
  const container = document.getElementById("messages");
  if (container) container.scrollTop = container.scrollHeight;
  scrollUnreadCount = 0;
  const btn = document.getElementById("scroll-bottom-btn");
  if (btn) btn.classList.add("hidden");
}

// ── Reply system ──────────────────────────────────────────────────────────────
function setReplyTo(msgId, senderDisplayName, content) {
  replyToMessage = { id: msgId, senderDisplayName, content };
  const bar = document.getElementById("reply-bar");
  if (!bar) return;
  bar.querySelector(".reply-preview").textContent =
    `Replying to ${senderDisplayName}: ${content.slice(0, 60)}${content.length > 60 ? "…" : ""}`;
  bar.classList.remove("hidden");
  document.getElementById("message-input").focus();
}

function clearReply() {
  replyToMessage = null;
  const bar = document.getElementById("reply-bar");
  if (bar) bar.classList.add("hidden");
}

// ── @mention autocomplete ─────────────────────────────────────────────────────
function onMentionInput() {
  const input = document.getElementById("message-input");
  const val   = input.value;
  const pos   = input.selectionStart;

  // Find the most recent "@" before cursor
  const before = val.slice(0, pos);
  const atPos  = before.lastIndexOf("@");
  if (atPos === -1 || (atPos > 0 && /\S/.test(val[atPos - 1]))) {
    hideMentionDropdown(); return;
  }
  const query = before.slice(atPos + 1).toLowerCase();
  mentionStart = atPos;

  // Gather candidates: online users + room members from sidebar
  const candidates = [];
  document.querySelectorAll(".dm-item").forEach(el => {
    const name = el.querySelector(".dm-name")?.textContent;
    if (name && name.toLowerCase().includes(query)) candidates.push(name);
  });
  // Also include any username we've seen in messages
  document.querySelectorAll(".msg-author").forEach(el => {
    const name = el.textContent;
    if (name && name.toLowerCase().includes(query) && !candidates.includes(name))
      candidates.push(name);
  });

  if (candidates.length === 0) { hideMentionDropdown(); return; }

  mentionItems = candidates.slice(0, 8);
  mentionIndex = 0;
  showMentionDropdown(input);
}

function showMentionDropdown(input) {
  let dropdown = document.getElementById("mention-dropdown");
  if (!dropdown) {
    dropdown = document.createElement("div");
    dropdown.id = "mention-dropdown";
    document.body.appendChild(dropdown);
  }
  dropdown.innerHTML = "";
  mentionItems.forEach((name, i) => {
    const item = document.createElement("div");
    item.className = "mention-item" + (i === mentionIndex ? " active" : "");
    item.textContent = "@" + name;
    item.addEventListener("mousedown", (e) => { e.preventDefault(); acceptMention(name); });
    dropdown.appendChild(item);
  });
  // Position above input
  const rect = input.getBoundingClientRect();
  dropdown.style.left   = rect.left + "px";
  dropdown.style.bottom = (window.innerHeight - rect.top + 4) + "px";
  dropdown.style.display = "block";
  mentionDropdownVisible = true;
}

function moveMentionSelection(delta) {
  if (!mentionDropdownVisible) return;
  mentionIndex = (mentionIndex + delta + mentionItems.length) % mentionItems.length;
  document.querySelectorAll("#mention-dropdown .mention-item").forEach((el, i) =>
    el.classList.toggle("active", i === mentionIndex));
}

function acceptMention(username) {
  const input = document.getElementById("message-input");
  const val   = input.value;
  const pos   = input.selectionStart;
  const before = val.slice(0, mentionStart);
  const after  = val.slice(pos);
  const insertion = `@${username} `;
  input.value = before + insertion + after;
  const newPos = before.length + insertion.length;
  input.setSelectionRange(newPos, newPos);
  hideMentionDropdown();
  input.focus();
}

function hideMentionDropdown() {
  const dropdown = document.getElementById("mention-dropdown");
  if (dropdown) dropdown.style.display = "none";
  mentionDropdownVisible = false;
  mentionItems = [];
  mentionIndex = -1;
}

// ── Markdown renderer (bold / italic / inline-code / blockquote only) ─────────
function renderMarkdown(raw) {
  // 1. Escape HTML first — then apply markdown transforms on the escaped string
  let s = escapeHtml(raw);
  // Blockquote lines ("> text")
  s = s.replace(/^&gt;\s?(.*)$/gm, '<div class="msg-blockquote">$1</div>');
  // Bold **text** (escaped: **text**)
  s = s.replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>");
  // Italic *text* (escaped: *text*)
  s = s.replace(/\*(.+?)\*/g, "<em>$1</em>");
  // Inline code `text`
  s = s.replace(/`([^`]+)`/g, '<code class="msg-code-inline">$1</code>');
  // Code block ```text```
  s = s.replace(/```([\s\S]*?)```/g, '<pre class="msg-code-block"><code>$1</code></pre>');
  // @mention highlighting (word boundary after @, letters/numbers/underscores)
  s = s.replace(/@([\w]+)/g, '<span class="msg-mention">@$1</span>');
  // Newlines → <br> (outside block elements)
  s = s.replace(/\n/g, "<br>");
  return s;
}

// ── Link preview (OG metadata fetch, LRU-capped) ────────────────────────────
const ogCache = new Map(); // url → preview object
const OG_CACHE_MAX = 500;  // max cached OG entries

function evictOgCache() {
  while (ogCache.size > OG_CACHE_MAX) {
    const oldest = ogCache.keys().next().value;
    ogCache.delete(oldest);
  }
}

async function fetchLinkPreview(url, targetEl) {
  if (ogCache.has(url)) {
    const data = ogCache.get(url);
    if (data) renderLinkPreview(data, url, targetEl);
    return;
  }
  try {
    const res = await apiFetch(`/api/og?url=${encodeURIComponent(url)}`);
    if (!res.ok) { ogCache.set(url, null); evictOgCache(); return; }
    const data = await res.json();
    ogCache.set(url, data.title ? data : null);
    evictOgCache();
    if (data.title) renderLinkPreview(data, url, targetEl);
  } catch { ogCache.set(url, null); evictOgCache(); }
}

function renderLinkPreview(data, url, targetEl) {
  const wrap = document.createElement("div");
  wrap.className = "link-preview";
  wrap.innerHTML = `
    <div class="lp-body">
      ${data.image ? `<img class="lp-image" src="${escapeHtml(data.image)}" alt="" loading="lazy" onerror="this.style.display='none'">` : ""}
      <div class="lp-text">
        <div class="lp-site">${escapeHtml(data.siteName || "")}</div>
        <a class="lp-title" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">${escapeHtml(data.title)}</a>
        ${data.description ? `<div class="lp-desc">${escapeHtml(data.description)}</div>` : ""}
      </div>
    </div>`;
  targetEl.appendChild(wrap);
}

function appendSystemMessage(text) {
  const container = document.getElementById("messages");
  const div = document.createElement("div");
  div.className = "message system";
  div.innerHTML = `<span>${escapeHtml(text)}</span>`;
  container.appendChild(div);
  container.scrollTop = container.scrollHeight;
}

// ── API Helper ────────────────────────────────────────────────────────────────
// Read the CSRF token from the non-HttpOnly cookie set by the server
function getCsrfToken() {
  const m = document.cookie.match(/(?:^|;\s*)csrfToken=([^;]+)/);
  return m ? decodeURIComponent(m[1]) : "";
}

async function apiFetch(url, method = "GET", body = null, auth = true) {
  const headers = { "Content-Type": "application/json" };
  // JWT is now in an HttpOnly cookie — include it automatically via credentials
  if (auth && token) headers["Authorization"] = `Bearer ${token}`;
  // Attach CSRF token for state-changing requests
  if (method !== "GET" && method !== "HEAD") {
    headers["X-CSRF-Token"] = getCsrfToken();
  }
  const res = await fetch(`${API_BASE}${url}`, {
    method,
    headers,
    credentials: "same-origin",   // send cookies (HttpOnly JWT + CSRF)
    body: body ? JSON.stringify(body) : undefined
  });
  // If the JWT has expired, force re-login immediately
  if (auth && res.status === 401) {
    logout();
    document.getElementById("login-error").textContent = "Session expired — please log in again.";
  }
  return res;
}

// ── Service Worker Registration + Push ──────────────────────────────────────
// ── PWA Install Prompt ───────────────────────────────────────────────────────
let _deferredInstallPrompt = null;

window.addEventListener("beforeinstallprompt", (e) => {
  e.preventDefault();
  _deferredInstallPrompt = e;
  // Show the install banner after a short delay (don't interrupt login)
  setTimeout(showInstallBanner, 3000);
});

function showInstallBanner() {
  if (!_deferredInstallPrompt) return;
  // Don't show if already installed (standalone mode)
  if (window.matchMedia("(display-mode: standalone)").matches) return;

  const banner = document.getElementById("pwa-install-banner");
  if (banner) banner.classList.remove("hidden");
}

function dismissInstallBanner() {
  const banner = document.getElementById("pwa-install-banner");
  if (banner) banner.classList.add("hidden");
  _deferredInstallPrompt = null;
}

async function installPwa() {
  if (!_deferredInstallPrompt) return;
  _deferredInstallPrompt.prompt();
  const { outcome } = await _deferredInstallPrompt.userChoice;
  _deferredInstallPrompt = null;
  const banner = document.getElementById("pwa-install-banner");
  if (banner) banner.classList.add("hidden");
}

// Hide the banner if installed while it's showing
window.addEventListener("appinstalled", () => {
  const banner = document.getElementById("pwa-install-banner");
  if (banner) banner.classList.add("hidden");
  _deferredInstallPrompt = null;
});

if ('serviceWorker' in navigator) {
  window.addEventListener('load', async () => {
    try {
      // Force the browser to check for an updated SW by using updateViaCache: 'none'
      const reg = await navigator.serviceWorker.register('/sw.js', { updateViaCache: 'none' });

      // Immediately check for an update (catches stale caches)
      reg.update().catch(() => {});

      // Listen for notification-click messages from SW
      navigator.serviceWorker.addEventListener('message', (e) => {
        if (e.data?.type === 'notification-click' && e.data.roomId) {
          // Try to switch to the notified room
          const roomEl = document.querySelector(`.room-item[data-id="${CSS.escape(e.data.roomId)}"]`);
          if (roomEl) roomEl.click();
        }
      });

      // Subscribe to push after login (deferred — called from showApp())
      window._swReg = reg;
    } catch { /* SW not available */ }
  });
}

async function subscribeToPush() {
  if (!window._swReg || !('PushManager' in window)) return;
  try {
    const permResult = await Notification.requestPermission();
    if (permResult !== 'granted') return;

    // Fetch VAPID public key
    const res = await apiFetch('/api/push/vapid-public-key', 'GET', null, false);
    if (!res.ok) return;
    const { publicKey } = await res.json();
    if (!publicKey || publicKey.startsWith('CHANGE_ME')) return;

    const existing = await window._swReg.pushManager.getSubscription();
    const sub = existing ?? await window._swReg.pushManager.subscribe({
      userVisibleOnly: true,
      applicationServerKey: urlBase64ToUint8Array(publicKey)
    });

    const json = sub.toJSON();
    await apiFetch('/api/push/subscribe', 'POST', {
      endpoint: sub.endpoint,
      p256dh:   json.keys?.p256dh   ?? '',
      auth:     json.keys?.auth     ?? ''
    });
  } catch { /* push not available or rejected */ }
}

function urlBase64ToUint8Array(base64String) {
  const padding = '='.repeat((4 - (base64String.length % 4)) % 4);
  const base64  = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
  const raw     = atob(base64);
  return Uint8Array.from([...raw].map(c => c.charCodeAt(0)));
}

// ── Swarm Background Animation (login page) ─────────────────────────────────
function initSwarm() {
  const canvas = document.getElementById("swarm-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");
  const COUNT = 72;
  const MAX_DIST = 140;
  let W, H, particles, rafId;

  function resize() {
    W = canvas.width  = canvas.offsetWidth;
    H = canvas.height = canvas.offsetHeight;
  }

  function makeParticle() {
    return {
      x:  Math.random() * W,
      y:  Math.random() * H,
      vx: (Math.random() - 0.5) * 0.55,
      vy: (Math.random() - 0.5) * 0.55,
      r:  Math.random() * 1.8 + 0.8
    };
  }

  function init() {
    resize();
    particles = Array.from({ length: COUNT }, makeParticle);
  }

  function step() {
    ctx.clearRect(0, 0, W, H);

    for (const p of particles) {
      p.x += p.vx;
      p.y += p.vy;
      if (p.x < 0 || p.x > W) p.vx *= -1;
      if (p.y < 0 || p.y > H) p.vy *= -1;
    }

    for (let i = 0; i < particles.length; i++) {
      for (let j = i + 1; j < particles.length; j++) {
        const a = particles[i], b = particles[j];
        const dx = a.x - b.x, dy = a.y - b.y;
        const dist = Math.sqrt(dx * dx + dy * dy);
        if (dist < MAX_DIST) {
          const alpha = (1 - dist / MAX_DIST) * 0.28;
          ctx.beginPath();
          ctx.moveTo(a.x, a.y);
          ctx.lineTo(b.x, b.y);
          ctx.strokeStyle = `rgba(88,101,242,${alpha})`;
          ctx.lineWidth = 1;
          ctx.stroke();
        }
      }
    }

    for (const p of particles) {
      ctx.beginPath();
      ctx.arc(p.x, p.y, p.r, 0, Math.PI * 2);
      ctx.fillStyle = "rgba(88,101,242,0.75)";
      ctx.fill();
    }

    rafId = requestAnimationFrame(step);
  }

  // Stop animation when auth overlay is hidden to save CPU
  const observer = new MutationObserver(() => {
    const hidden = document.getElementById("auth-overlay")?.classList.contains("hidden");
    if (hidden && rafId) { cancelAnimationFrame(rafId); rafId = null; }
    else if (!hidden && !rafId) { step(); }
  });
  const overlay = document.getElementById("auth-overlay");
  if (overlay) observer.observe(overlay, { attributes: true, attributeFilter: ["class"] });

  init();
  step();
  window.addEventListener("resize", resize);
}

// ── Security Helpers ──────────────────────────────────────────────────────────
function escapeHtml(str) {
  return String(str ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

/** Validate a hex colour string — returns the colour if valid, or a fallback.
 *  Prevents CSS injection if an avatar colour somehow bypasses server checks. */
function safeColor(color, fallback = "#5865f2") {
  return /^#[0-9a-fA-F]{6}$/.test(color) ? color : fallback;
}
