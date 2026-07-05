/* =====================================================================
   AI Chat frontend. Pure rendering/interaction layer (spec §2):
   every provider call happens in C#; this file only talks to the host
   through window.chrome.webview.postMessage / message events.
   ===================================================================== */

"use strict";

// ------------------------------------------------------------------ Bridge

const bridge = {
  send(cmd, payload = {}) {
    const requestId = "r" + Math.random().toString(36).slice(2, 10);
    window.chrome?.webview?.postMessage({ cmd, requestId, payload });
    return requestId;
  },
  on(handlers) {
    window.chrome?.webview?.addEventListener("message", (e) => {
      const msg = e.data;
      if (msg && msg.event && handlers[msg.event]) handlers[msg.event](msg.data || {});
    });
  },
};

// ------------------------------------------------------------------ State

const state = {
  sessions: [],
  providers: [],                 // { name, requiresApiKey, hasApiKey }
  modelsByProvider: {},          // provider -> ModelInfo[]
  templates: [],
  settings: { theme: "light", codeFontSize: 13 },

  currentSessionId: null,
  currentSession: null,
  messages: [],                  // DTOs of the open session

  streaming: false,
  streamMessageId: null,
  streamRaw: "",
  streamThinking: "",

  pendingAttachments: [],        // { fileName, mimeType, dataBase64 }
  sessionCostUsd: 0,
  sessionTokens: 0,
};

// message-ID ↔ DOM-anchor map, kept current by an IntersectionObserver so
// copy-button/scroll behaviour stays correct while streaming shifts the DOM
// (spec §3.3) — not raw DOM positions.
const anchorMap = new Map();     // messageId -> element
const visibleMessages = new Set();
const io = new IntersectionObserver((entries) => {
  for (const en of entries) {
    const id = en.target.dataset.messageId;
    if (!id) continue;
    if (en.isIntersecting) visibleMessages.add(id);
    else visibleMessages.delete(id);
  }
}, { root: null, threshold: 0 });

// Raw source of every rendered code block, keyed by generated id, so copy /
// download never depend on re-extracting text from highlighted DOM.
const codeStore = new Map();
let codeSeq = 0;

/** Streaming re-renders mint new code-block ids each frame; drop entries whose
 *  DOM no longer exists so the store can't grow without bound. */
function purgeCodeStore() {
  if (codeStore.size < 400) return;
  for (const id of [...codeStore.keys()]) {
    if (!document.querySelector(`[data-code-id="${id}"]`)) codeStore.delete(id);
  }
}

// ------------------------------------------------------------------ DOM

const $ = (sel) => document.querySelector(sel);
const el = {
  sidebar: $("#sidebar"),
  sessionList: $("#session-list"),
  searchInput: $("#search-input"),
  searchResults: $("#search-results"),
  chatScroll: $("#chat-scroll"),
  messages: $("#messages"),
  emptyState: $("#empty-state"),
  chatTitle: $("#chat-title"),
  input: $("#input"),
  composerBox: $("#composer-box"),
  btnSend: $("#btn-send"),
  btnStop: $("#btn-stop"),
  btnAttach: $("#btn-attach"),
  attachmentStrip: $("#attachment-strip"),
  tokenEstimate: $("#token-estimate"),
  sessionCost: $("#session-cost"),
  providerSelect: $("#provider-select"),
  modelSelect: $("#model-select"),
  thinkingSelect: $("#thinking-select"),
  runCommandRows: $("#run-command-rows"),
  runTimeout: $("#run-timeout"),
  confirmRun: $("#confirm-run"),
  exportMenu: $("#export-menu"),
  lightbox: $("#lightbox"),
  lightboxImg: $("#lightbox-img"),
  settingsModal: $("#settings-modal"),
  apiKeyRows: $("#api-key-rows"),
  promptModal: $("#prompt-modal"),
  systemPromptText: $("#system-prompt-text"),
  templateSelect: $("#template-select"),
  toasts: $("#toasts"),
  codeFontSize: $("#code-font-size"),
  codeFontSizeValue: $("#code-font-size-value"),
};

// ------------------------------------------------------------------ Markdown

marked.use({
  gfm: true,
  breaks: true,
  renderer: {
    code(code, infostring) {
      // Filename convention for generated files (spec §3.4): ```python:script.py
      const info = (infostring || "").trim();
      let lang = info, fileName = null;
      const colon = info.indexOf(":");
      if (colon > -1) { lang = info.slice(0, colon); fileName = info.slice(colon + 1).trim() || null; }
      if (!fileName) {
        const m = code.match(/^<file name="([^"]+)">\n?([\s\S]*?)\n?<\/file>$/);
        if (m) { fileName = m[1]; code = m[2]; }
      }

      const id = "c" + (++codeSeq);
      codeStore.set(id, { code, lang, fileName });

      let highlighted;
      try {
        highlighted = lang && hljs.getLanguage(lang)
          ? hljs.highlight(code, { language: lang }).value
          : hljs.highlightAuto(code).value;
      } catch { highlighted = escapeHtml(code); }

      // Run button only for languages with a configured run command
      // (feature: execute code locally; the WebView runs on this machine).
      const runnable = lang && state.settings.runCommands &&
        Object.keys(state.settings.runCommands).some((k) => k.toLowerCase() === lang.toLowerCase());

      const buttons = [
        runnable ? `<button data-act="run" data-code-id="${id}" title="Execute on this machine">▶ Run</button>` : "",
        `<button data-act="copy" data-code-id="${id}">Copy</button>`,
        fileName ? `<button data-act="download" data-code-id="${id}">Download</button>` : "",
        fileName ? `<button data-act="save-native" data-code-id="${id}">Save as…</button>` : "",
      ].join("");

      // The .code-head is position:sticky against the chat scroller, so these
      // buttons follow while you scroll through the block and vanish at its end.
      return `
<div class="code-block" data-code-id="${id}">
  <div class="code-head">
    <span class="code-lang">${escapeHtml(lang || "text")}</span>
    ${fileName ? `<span class="code-file">${escapeHtml(fileName)}</span>` : ""}
    <span class="code-head-actions">${buttons}</span>
  </div>
  <pre><code class="hljs">${highlighted}</code></pre>
</div>`;
    },
  },
});

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

/** Markdown → sanitized HTML (DOMPurify before DOM injection, spec §3.5). */
function renderMarkdown(md) {
  const html = marked.parse(md || "");
  return DOMPurify.sanitize(html, {
    ADD_ATTR: ["data-act", "data-code-id", "target"],
    ALLOWED_URI_REGEXP: /^(?:(?:https?|data):|[^a-z+.\-]*(?:[^a-z+.\-:]|$))/i,
  });
}

// ------------------------------------------------------------------ Toasts

function toast(text, kind = "info", ms = 4200) {
  const t = document.createElement("div");
  t.className = "toast" + (kind === "error" ? " error" : "");
  t.textContent = text;
  el.toasts.appendChild(t);
  setTimeout(() => t.remove(), ms);
}

// ------------------------------------------------------------------ Sessions sidebar

function renderSessions() {
  el.sessionList.innerHTML = "";
  const pinned = state.sessions.filter((s) => s.pinned);
  const rest = state.sessions.filter((s) => !s.pinned);

  const addGroup = (label, items) => {
    if (!items.length) return;
    const lab = document.createElement("div");
    lab.className = "session-group-label";
    lab.textContent = label;
    el.sessionList.appendChild(lab);
    items.forEach((s) => el.sessionList.appendChild(sessionItem(s)));
  };
  addGroup("Pinned", pinned);
  addGroup("Chats", rest);
}

function sessionItem(s) {
  const item = document.createElement("div");
  item.className = "session-item" + (s.id === state.currentSessionId ? " active" : "");
  item.innerHTML = `
    ${s.pinned ? '<span class="pin-mark">★</span>' : ""}
    <span class="title"></span>
    ${s.tags ? `<span class="tags"></span>` : ""}
    <span class="item-actions">
      <button data-a="pin" title="${s.pinned ? "Unpin" : "Pin"}">${s.pinned ? "★" : "☆"}</button>
      <button data-a="rename" title="Rename">✎</button>
      <button data-a="tag" title="Tags">#</button>
      <button data-a="del" title="Delete">🗑</button>
    </span>`;
  item.querySelector(".title").textContent = s.title;
  if (s.tags) item.querySelector(".tags").textContent = s.tags;

  item.addEventListener("click", (e) => {
    const a = e.target.closest("button")?.dataset.a;
    if (a === "pin") { bridge.send("pinSession", { sessionId: s.id, pinned: !s.pinned }); return; }
    if (a === "rename") {
      const title = prompt("Rename chat", s.title);
      if (title) bridge.send("renameSession", { sessionId: s.id, title });
      return;
    }
    if (a === "tag") {
      const tags = prompt("Tags (comma separated)", s.tags || "");
      if (tags !== null) bridge.send("tagSession", { sessionId: s.id, tags });
      return;
    }
    if (a === "del") {
      if (confirm(`Delete "${s.title}"? This cannot be undone.`)) {
        bridge.send("deleteSession", { sessionId: s.id });
      }
      return;
    }
    openSession(s.id);
  });
  return item;
}

function openSession(id) {
  state.currentSessionId = id;
  state.sessionCostUsd = 0;
  state.sessionTokens = 0;
  bridge.send("loadSession", { sessionId: id });
  renderSessions();
}

// ------------------------------------------------------------------ Message rendering

function clearMessages() {
  io.disconnect();
  anchorMap.clear();
  visibleMessages.clear();
  el.messages.innerHTML = "";
}

function thinkingHtml(text, open) {
  return `<details class="thinking-block"${open ? " open" : ""}>
    <summary>Thinking</summary>
    <div class="thinking-body">${escapeHtml(text)}</div>
  </details>`;
}

function messageElement(m) {
  const wrap = document.createElement("div");
  wrap.className = "msg " + m.role;
  wrap.dataset.messageId = m.id;

  const who = m.role === "user" ? "You" : "Assistant";
  const meta = [];
  if (m.model) meta.push(m.model);
  if (m.createdAt) meta.push(new Date(m.createdAt).toLocaleString());

  wrap.innerHTML = `
    <div class="msg-head">
      <span class="who">${who}</span>
      <span class="meta">${escapeHtml(meta.join(" · "))}</span>
    </div>
    <div class="attachment-chip-row"></div>
    <div class="bubble"></div>
    <div class="usage-line"></div>
    <div class="msg-foot"></div>
    <span class="msg-actions"></span>`;

  // Attachments (spec §3.5): render by stored MIME type, images inline + lightbox.
  const chipRow = wrap.querySelector(".attachment-chip-row");
  (m.attachments || []).forEach((a) => {
    if (a.dataUrl) {
      const img = document.createElement("img");
      img.className = "attachment-thumb";
      img.src = a.dataUrl;
      img.alt = a.fileName || "image";
      img.addEventListener("click", () => showLightbox(a.dataUrl));
      chipRow.appendChild(img);
    } else if (a.fileName) {
      const chip = document.createElement("span");
      chip.className = "attachment-chip";
      chip.textContent = `📎 ${a.fileName}`;
      chipRow.appendChild(chip);
    }
  });
  if (!chipRow.children.length) chipRow.remove();

  const bubble = wrap.querySelector(".bubble");
  if (m.role === "assistant") {
    // Reasoning renders as a collapsed, expandable block above the answer.
    bubble.innerHTML = (m.thinking ? thinkingHtml(m.thinking, false) : "") + renderMarkdown(m.content);
  } else {
    bubble.textContent = m.content;
  }

  const foot = wrap.querySelector(".msg-foot");
  const actions = wrap.querySelector(".msg-actions");
  if (m.role === "user") {
    foot.remove();
    actions.innerHTML = `<button data-a="edit">Edit</button><button data-a="branch">Branch here</button>`;
    actions.addEventListener("click", (e) => {
      const a = e.target.closest("button")?.dataset.a;
      if (a === "edit") beginEdit(wrap, m);
      if (a === "branch") bridge.send("branchSession", { sessionId: state.currentSessionId, messageId: m.id });
    });
  } else {
    // Copy / Regenerate / Branch sit at the END of every assistant message;
    // CSS shows Regenerate only on the newest one.
    actions.remove();
    foot.innerHTML = `
      <button data-a="copy-msg" title="Copy message">⧉ Copy</button>
      <button data-a="regen" class="btn-regen" title="Regenerate this answer">↻ Regenerate</button>
      <button data-a="branch" title="Branch the conversation from here">⑂ Branch here</button>`;
    foot.addEventListener("click", (e) => {
      const a = e.target.closest("button")?.dataset.a;
      if (a === "branch") bridge.send("branchSession", { sessionId: state.currentSessionId, messageId: m.id });
      if (a === "regen") bridge.send("regenerate", { sessionId: state.currentSessionId, ...thinkingPayload() });
      if (a === "copy-msg") navigator.clipboard.writeText(m.content).then(() => toast("Message copied"));
    });
  }

  anchorMap.set(m.id, wrap);
  io.observe(wrap);
  return wrap;
}

function renderAllMessages() {
  clearMessages();
  state.messages.forEach((m) => el.messages.appendChild(messageElement(m)));
  el.emptyState.classList.toggle("hidden", state.messages.length > 0);
  scrollToBottom(true);
  purgeCodeStore();
}

function beginEdit(wrap, m) {
  if (state.streaming) return toast("Wait for the current response to finish.", "error");
  const bubble = wrap.querySelector(".bubble");
  const original = m.content;
  bubble.innerHTML = `
    <div class="edit-area">
      <textarea></textarea>
      <div class="edit-buttons">
        <button class="btn-primary" data-a="save" style="width:auto">Save &amp; resend</button>
        <button class="btn-ghost" data-a="cancel">Cancel</button>
      </div>
    </div>`;
  const ta = bubble.querySelector("textarea");
  ta.value = original;
  ta.focus();
  bubble.querySelector('[data-a="save"]').addEventListener("click", () => {
    const text = ta.value.trim();
    if (!text) return;
    // Fork the conversation from this point (spec §3.1): the host rewrites the
    // message, drops everything after it, and streams a fresh reply.
    bridge.send("editAndResend", { sessionId: state.currentSessionId, messageId: m.id, text, ...thinkingPayload() });
  });
  bubble.querySelector('[data-a="cancel"]').addEventListener("click", () => {
    bubble.textContent = original;
  });
}

// ------------------------------------------------------------------ Scrolling

function isNearBottom() {
  const c = el.chatScroll;
  return c.scrollHeight - c.scrollTop - c.clientHeight < 90;
}
function scrollToBottom(force = false) {
  if (force || isNearBottom()) el.chatScroll.scrollTop = el.chatScroll.scrollHeight;
}

// ------------------------------------------------------------------ Streaming UI

let renderQueued = false;
function queueStreamRender() {
  if (renderQueued) return;
  renderQueued = true;
  requestAnimationFrame(() => {
    renderQueued = false;
    const wrap = anchorMap.get(state.streamMessageId);
    if (!wrap) return;
    const pinned = isNearBottom();
    const bubble = wrap.querySelector(".bubble");

    // Preserve the open/closed state of the thinking block across re-renders.
    const wasOpen = bubble.querySelector(".thinking-block")?.open ?? true;
    bubble.innerHTML =
      (state.streamThinking ? thinkingHtml(state.streamThinking, wasOpen) : "") +
      renderMarkdown(state.streamRaw) +
      '<span class="typing-dots"></span>';

    // Keep the live thinking view pinned to its newest line.
    const tb = bubble.querySelector(".thinking-body");
    if (tb) tb.scrollTop = tb.scrollHeight;

    if (pinned) scrollToBottom(true);
  });
}

function setStreaming(on) {
  state.streaming = on;
  el.btnSend.classList.toggle("hidden", on);
  el.btnStop.classList.toggle("hidden", !on);
  el.input.disabled = false;
}

// ------------------------------------------------------------------ Composer

function autoSizeInput() {
  el.input.style.height = "auto";
  el.input.style.height = Math.min(el.input.scrollHeight, 220) + "px";
  const tokens = Math.max(1, Math.round((el.input.value.length || 0) / 4));
  el.tokenEstimate.textContent = el.input.value ? `~${tokens} tokens` : "";
}

function sendCurrent() {
  const text = el.input.value.trim();
  if ((!text && !state.pendingAttachments.length) || state.streaming) return;
  if (!state.currentSessionId) return createSessionThenSend(text);

  bridge.send("sendMessage", {
    sessionId: state.currentSessionId,
    text,
    provider: el.providerSelect.value || undefined,
    model: el.modelSelect.value || undefined,
    attachments: state.pendingAttachments,
  });
  el.input.value = "";
  autoSizeInput();
  state.pendingAttachments = [];
  renderAttachmentStrip();
}

let pendingFirstText = null;
function createSessionThenSend(text) {
  pendingFirstText = text;
  bridge.send("createSession", {
    provider: el.providerSelect.value || "anthropic",
    model: el.modelSelect.value || undefined,
  });
}

// ------------------------------------------------------------------ Attachments

function renderAttachmentStrip() {
  el.attachmentStrip.innerHTML = "";
  el.attachmentStrip.classList.toggle("hidden", !state.pendingAttachments.length);
  state.pendingAttachments.forEach((a, i) => {
    const chip = document.createElement("span");
    chip.className = "attachment-chip";
    chip.innerHTML = `📎 <span></span> <button title="Remove" style="border:none;background:none;color:inherit;cursor:pointer">✕</button>`;
    chip.querySelector("span").textContent = a.fileName;
    chip.querySelector("button").addEventListener("click", () => {
      state.pendingAttachments.splice(i, 1);
      renderAttachmentStrip();
    });
    el.attachmentStrip.appendChild(chip);
  });
}

function addFileAsAttachment(file) {
  if (file.size > 20 * 1024 * 1024) return toast(`${file.name} exceeds the 20 MB limit.`, "error");
  const reader = new FileReader();
  reader.onload = () => {
    state.pendingAttachments.push({
      fileName: file.name,
      mimeType: file.type || "application/octet-stream",
      dataBase64: String(reader.result).split(",")[1],
    });
    renderAttachmentStrip();
  };
  reader.readAsDataURL(file);
}

// ------------------------------------------------------------------ Code block actions
// Event delegation: copy / JS-blob download / native save. Blob URLs are
// revoked right after the click to avoid leaks (spec §3.4).

// ------------------------------------------------------------------ Local code execution
// Runs happen on this machine through user-configured commands (Settings → Run
// commands). Output streams line-by-line from C# into a panel under the block.

const runs = new Map(); // runId -> { block, out }
let runSeq = 0;

function startRun(entry, block) {
  if (state.settings.confirmRun !== false &&
      !confirm(`Run this ${entry.lang || "code"} block on this machine?\n\nOnly run code you trust.`)) return;

  const runId = "run" + Date.now().toString(36) + (++runSeq);

  let out = block.querySelector(".run-output");
  if (!out) {
    out = document.createElement("div");
    out.className = "run-output";
    block.appendChild(out);
    block.classList.add("has-output");
  }
  out.innerHTML = `<div class="run-status">Starting… <button class="run-stop">Stop</button></div>`;
  out.querySelector(".run-stop").addEventListener("click", () => bridge.send("stopRun", { runId }));

  runs.set(runId, { block, out });
  bridge.send("runCode", { runId, language: entry.lang, code: entry.code });
}

function appendRunLine(out, text, isStderr) {
  const line = document.createElement("div");
  line.className = "run-line" + (isStderr ? " stderr" : "");
  line.textContent = text;
  out.insertBefore(line, out.querySelector(".run-status"));
  out.scrollTop = out.scrollHeight;
}

document.addEventListener("click", (e) => {
  const btn = e.target.closest("[data-act]");
  if (!btn) return;
  const entry = codeStore.get(btn.dataset.codeId);
  if (!entry) return;

  if (btn.dataset.act === "run") {
    const block = btn.closest(".code-block");
    if (block) startRun(entry, block);
  }

  if (btn.dataset.act === "copy") {
    navigator.clipboard.writeText(entry.code).then(() => {
      btn.classList.add("copied");
      btn.textContent = "Copied";
      setTimeout(() => { btn.classList.remove("copied"); btn.textContent = "Copy"; }, 1500);
    });
  }

  if (btn.dataset.act === "download") {
    // Text files: Blob + <a download> entirely in JS — no C# round-trip.
    const blob = new Blob([entry.code], { type: "text/plain;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = entry.fileName || `snippet.${entry.lang || "txt"}`;
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
  }

  if (btn.dataset.act === "save-native") {
    // Native SaveFileDialog path through C# (spec §3.4).
    bridge.send("saveBinaryFile", {
      fileName: entry.fileName || "snippet.txt",
      dataBase64: btoa(unescape(encodeURIComponent(entry.code))),
    });
  }
});

// Lightbox for any image inside a rendered message (spec §3.5).
el.messages.addEventListener("click", (e) => {
  if (e.target.tagName === "IMG" && e.target.closest(".bubble")) showLightbox(e.target.src);
});
function showLightbox(src) {
  el.lightboxImg.src = src;
  el.lightbox.classList.remove("hidden");
}
el.lightbox.addEventListener("click", () => el.lightbox.classList.add("hidden"));

// ------------------------------------------------------------------ Provider / model pickers

function renderProviderSelect() {
  el.providerSelect.innerHTML = "";
  state.providers.forEach((p) => {
    const opt = document.createElement("option");
    opt.value = p.name;
    opt.textContent = p.name + (p.requiresApiKey && !p.hasApiKey ? " (no key)" : "");
    el.providerSelect.appendChild(opt);
  });
}

function renderModelSelect(provider, preferred) {
  el.modelSelect.innerHTML = "";
  const models = state.modelsByProvider[provider] || [];
  models.forEach((m) => {
    const opt = document.createElement("option");
    opt.value = m.id;
    opt.textContent = m.displayName || m.id;
    el.modelSelect.appendChild(opt);
  });
  if (preferred && models.some((m) => m.id === preferred)) el.modelSelect.value = preferred;
}

function persistSessionModel() {
  if (!state.currentSessionId) return;
  bridge.send("setSessionModel", {
    sessionId: state.currentSessionId,
    provider: el.providerSelect.value,
    model: el.modelSelect.value,
  });
}

el.providerSelect.addEventListener("change", () => {
  const p = el.providerSelect.value;
  if (!state.modelsByProvider[p]) bridge.send("listModels", { provider: p });
  renderModelSelect(p);
  persistSessionModel();
  updateThinkingSelect();
});
el.modelSelect.addEventListener("change", () => { persistSessionModel(); updateThinkingSelect(); });

// ------------------------------------------------------------------ Thinking / reasoning
// The selector only appears when the selected model actually supports a thinking
// control (per requirement: "if the selected model doesn't have these, don't show").

function currentModelInfo() {
  const models = state.modelsByProvider[el.providerSelect.value] || [];
  return models.find((m) => m.id === el.modelSelect.value) || null;
}

function updateThinkingSelect() {
  const info = currentModelInfo();
  const kind = info?.supportsThinking ? info.thinkingKind : null;

  if (!kind) { el.thinkingSelect.classList.add("hidden"); return; }

  const anthropic = kind === "anthropic";
  const options = anthropic
    ? [
        ["off|medium", "Thinking: off"],
        ["adaptive|medium", "Thinking: adaptive"],
        ["enabled|low", "Thinking: low"],
        ["enabled|medium", "Thinking: medium"],
        ["enabled|high", "Thinking: high"],
      ]
    : [
        ["off|medium", "Thinking: off"],
        ["enabled|low", "Effort: low"],
        ["enabled|medium", "Effort: medium"],
        ["enabled|high", "Effort: high"],
      ];

  el.thinkingSelect.innerHTML = "";
  options.forEach(([v, label]) => {
    const opt = document.createElement("option");
    opt.value = v;
    opt.textContent = label;
    el.thinkingSelect.appendChild(opt);
  });

  // Restore the persisted preference where it exists in this model's option set.
  const saved = `${state.settings.thinkingMode || "off"}|${state.settings.thinkingEffort || "medium"}`;
  el.thinkingSelect.value = options.some(([v]) => v === saved) ? saved : "off|medium";
  el.thinkingSelect.classList.remove("hidden");
}

el.thinkingSelect.addEventListener("change", () => {
  const [mode, effort] = el.thinkingSelect.value.split("|");
  state.settings.thinkingMode = mode;
  state.settings.thinkingEffort = effort;
  bridge.send("setSetting", { key: "thinkingMode", value: mode });
  bridge.send("setSetting", { key: "thinkingEffort", value: effort });
});

/** Flat fields merged into sendMessage / regenerate / editAndResend payloads. */
function thinkingPayload() {
  if (el.thinkingSelect.classList.contains("hidden")) return {};
  const [mode, effort] = el.thinkingSelect.value.split("|");
  if (mode === "off") return {};
  return { thinkingMode: mode, thinkingEffort: effort };
}

// ------------------------------------------------------------------ Settings modal

function renderApiKeyRows() {
  el.apiKeyRows.innerHTML = "";
  state.providers.filter((p) => p.requiresApiKey).forEach((p) => {
    const row = document.createElement("div");
    row.className = "key-row";
    row.innerHTML = `
      <span class="key-name">${escapeHtml(p.name)}</span>
      <input type="password" placeholder="${p.hasApiKey ? "•••••••• (saved)" : "paste API key"}" autocomplete="off" />
      <span class="key-status ${p.hasApiKey ? "ok" : ""}">${p.hasApiKey ? "✓ saved" : "not set"}</span>
      <button data-a="save">Save</button>
      <button data-a="remove">Remove</button>`;
    const input = row.querySelector("input");
    row.querySelector('[data-a="save"]').addEventListener("click", () => {
      const key = input.value.trim();
      if (!key) return toast("Paste a key first.", "error");
      bridge.send("saveApiKey", { provider: p.name, apiKey: key });
      input.value = "";
    });
    row.querySelector('[data-a="remove"]').addEventListener("click", () => {
      if (confirm(`Remove the saved ${p.name} key?`)) bridge.send("deleteApiKey", { provider: p.name });
    });
    el.apiKeyRows.appendChild(row);
  });
}

function applySettings() {
  document.documentElement.dataset.theme = state.settings.theme;
  document.documentElement.style.setProperty("--code-font-size", state.settings.codeFontSize + "px");
  $("#hljs-theme-light").disabled = state.settings.theme === "dark";
  $("#hljs-theme-dark").disabled = state.settings.theme !== "dark";
  el.codeFontSize.value = state.settings.codeFontSize;
  el.codeFontSizeValue.textContent = state.settings.codeFontSize + "px";
}

el.codeFontSize.addEventListener("input", () => {
  state.settings.codeFontSize = Number(el.codeFontSize.value);
  applySettings();
  bridge.send("setSetting", { key: "codeFontSize", value: String(state.settings.codeFontSize) });
});

$("#btn-theme").addEventListener("click", () => {
  state.settings.theme = state.settings.theme === "dark" ? "light" : "dark";
  applySettings();
  bridge.send("setSetting", { key: "theme", value: state.settings.theme });
});

// ------------------------------------------------------------------ Run commands (settings)
// Each language maps to { extension, command } where "{file}" is the temp file path.

function runCommandRow(lang = "", ext = "", command = "") {
  const row = document.createElement("div");
  row.className = "run-cmd-row";
  row.innerHTML = `
    <input class="rc-lang" placeholder="language" />
    <input class="rc-ext" placeholder=".py" />
    <input class="rc-cmd" placeholder='python "{file}"' />
    <button title="Remove">✕</button>`;
  row.querySelector(".rc-lang").value = lang;
  row.querySelector(".rc-ext").value = ext;
  row.querySelector(".rc-cmd").value = command;
  row.querySelector("button").addEventListener("click", () => row.remove());
  return row;
}

function renderRunCommandRows() {
  if (!el.runCommandRows) return;
  el.runCommandRows.innerHTML = "";
  const cmds = state.settings.runCommands || {};
  Object.keys(cmds).sort().forEach((lang) => {
    const c = cmds[lang] || {};
    el.runCommandRows.appendChild(runCommandRow(lang, c.extension || "", c.command || ""));
  });
  el.runTimeout.value = state.settings.runTimeoutSeconds ?? 60;
  el.confirmRun.checked = state.settings.confirmRun !== false;
}

$("#btn-add-run-command").addEventListener("click", () =>
  el.runCommandRows.appendChild(runCommandRow()));

$("#btn-save-run-commands").addEventListener("click", () => {
  const cmds = {};
  el.runCommandRows.querySelectorAll(".run-cmd-row").forEach((row) => {
    const lang = row.querySelector(".rc-lang").value.trim().toLowerCase();
    const extension = row.querySelector(".rc-ext").value.trim();
    const command = row.querySelector(".rc-cmd").value.trim();
    if (lang && command) cmds[lang] = { extension, command };
  });
  state.settings.runCommands = cmds;

  const timeout = Math.max(1, parseInt(el.runTimeout.value, 10) || 60);
  state.settings.runTimeoutSeconds = timeout;
  state.settings.confirmRun = el.confirmRun.checked;

  bridge.send("setSetting", { key: "runCommands", value: JSON.stringify(cmds) });
  bridge.send("setSetting", { key: "runTimeoutSeconds", value: String(timeout) });
  bridge.send("setSetting", { key: "confirmRun", value: el.confirmRun.checked ? "true" : "false" });
  toast("Run settings saved. Run buttons update on new messages.");
});

// ------------------------------------------------------------------ System prompt / templates

function renderTemplateSelect() {
  el.templateSelect.innerHTML = '<option value="">— custom —</option>';
  state.templates.forEach((t) => {
    const opt = document.createElement("option");
    opt.value = t.id;
    opt.textContent = t.name;
    el.templateSelect.appendChild(opt);
  });
}

el.templateSelect.addEventListener("change", () => {
  const t = state.templates.find((x) => x.id === el.templateSelect.value);
  if (t) el.systemPromptText.value = t.prompt;
});

$("#btn-save-prompt").addEventListener("click", () => {
  if (!state.currentSessionId) return toast("Open or start a chat first.", "error");
  bridge.send("setSystemPrompt", {
    sessionId: state.currentSessionId,
    systemPrompt: el.systemPromptText.value,
  });
  el.promptModal.classList.add("hidden");
  toast("System prompt saved for this chat.");
});

$("#btn-save-template").addEventListener("click", () => {
  const name = prompt("Template name", "");
  if (!name) return;
  bridge.send("saveTemplate", { name, prompt: el.systemPromptText.value });
});

// ------------------------------------------------------------------ Header buttons

$("#btn-new-chat").addEventListener("click", () => {
  pendingFirstText = null;
  bridge.send("createSession", {
    provider: el.providerSelect.value || "anthropic",
    model: el.modelSelect.value || undefined,
  });
});

$("#btn-toggle-sidebar").addEventListener("click", () => el.sidebar.classList.toggle("collapsed"));

$("#btn-branch").addEventListener("click", () => {
  if (state.currentSessionId) bridge.send("branchSession", { sessionId: state.currentSessionId });
});

$("#btn-system-prompt").addEventListener("click", () => {
  if (!state.currentSessionId) return toast("Open or start a chat first.", "error");
  el.systemPromptText.value = state.currentSession?.systemPrompt || "";
  el.templateSelect.value = "";
  el.promptModal.classList.remove("hidden");
});

$("#btn-export").addEventListener("click", (e) => {
  e.stopPropagation();
  el.exportMenu.classList.toggle("hidden");
});
document.addEventListener("click", () => el.exportMenu.classList.add("hidden"));
el.exportMenu.addEventListener("click", (e) => {
  const f = e.target.closest("button")?.dataset.format;
  if (f && state.currentSessionId) bridge.send("exportSession", { sessionId: state.currentSessionId, format: f });
});

$("#btn-import").addEventListener("click", () => bridge.send("importSession"));
$("#btn-settings").addEventListener("click", () => el.settingsModal.classList.remove("hidden"));

document.querySelectorAll(".close-modal").forEach((b) =>
  b.addEventListener("click", () => b.closest(".overlay").classList.add("hidden")));
[el.settingsModal, el.promptModal].forEach((ov) =>
  ov.addEventListener("click", (e) => { if (e.target === ov) ov.classList.add("hidden"); }));

el.chatTitle.addEventListener("dblclick", () => {
  if (!state.currentSessionId) return;
  const title = prompt("Rename chat", el.chatTitle.textContent);
  if (title) bridge.send("renameSession", { sessionId: state.currentSessionId, title });
});

// ------------------------------------------------------------------ Composer wiring

el.input.addEventListener("input", autoSizeInput);
el.input.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendCurrent(); }
});
el.btnSend.addEventListener("click", sendCurrent);
el.btnStop.addEventListener("click", () => {
  if (state.currentSessionId) bridge.send("stopGeneration", { sessionId: state.currentSessionId });
});
el.btnAttach.addEventListener("click", () => bridge.send("pickAttachment"));

// Drag & drop attachments (spec §4 multimodal input).
["dragover", "dragleave", "drop"].forEach((evt) =>
  el.composerBox.addEventListener(evt, (e) => {
    e.preventDefault();
    el.composerBox.classList.toggle("dragover", evt === "dragover");
    if (evt === "drop") [...(e.dataTransfer?.files || [])].forEach(addFileAsAttachment);
  }));

// ------------------------------------------------------------------ Search (FTS5, spec §3.2)

let searchTimer = null;
el.searchInput.addEventListener("input", () => {
  clearTimeout(searchTimer);
  const q = el.searchInput.value.trim();
  if (!q) { el.searchResults.classList.add("hidden"); el.searchResults.innerHTML = ""; return; }
  searchTimer = setTimeout(() => bridge.send("search", { query: q }), 220);
});

function renderSearchResults(hits) {
  el.searchResults.innerHTML = "";
  el.searchResults.classList.remove("hidden");
  if (!hits.length) {
    el.searchResults.innerHTML = '<div class="search-hit">No matches.</div>';
    return;
  }
  hits.forEach((h) => {
    const div = document.createElement("div");
    div.className = "search-hit";
    div.innerHTML = `<div class="hit-title"></div><div class="hit-snippet">${DOMPurify.sanitize(h.snippet, { ALLOWED_TAGS: ["mark"] })}</div>`;
    div.querySelector(".hit-title").textContent = `${h.sessionTitle} · ${h.role}`;
    div.addEventListener("click", () => {
      el.searchInput.value = "";
      el.searchResults.classList.add("hidden");
      openSession(h.sessionId);
    });
    el.searchResults.appendChild(div);
  });
}

// ------------------------------------------------------------------ Host events (C# → JS)

bridge.on({
  bootstrap(d) {
    state.sessions = d.sessions || [];
    state.providers = d.providers || [];
    state.templates = d.templates || [];
    state.settings = { ...state.settings, ...(d.settings || {}) };
    applySettings();
    renderSessions();
    renderProviderSelect();
    renderApiKeyRows();
    renderTemplateSelect();
    renderRunCommandRows();
    if (state.sessions.length) openSession(state.sessions[0].id);
  },

  sessions(d) {
    state.sessions = d.sessions || [];
    renderSessions();
    const cur = state.sessions.find((s) => s.id === state.currentSessionId);
    if (cur) { state.currentSession = cur; el.chatTitle.textContent = cur.title; }
  },

  sessionCreated(d) {
    openSession(d.session.id);
    if (pendingFirstText !== null) {
      const text = pendingFirstText;
      pendingFirstText = null;
      setTimeout(() => {
        bridge.send("sendMessage", {
          sessionId: d.session.id,
          text,
          provider: el.providerSelect.value || undefined,
          model: el.modelSelect.value || undefined,
          attachments: state.pendingAttachments,
          ...thinkingPayload(),
        });
        el.input.value = "";
        autoSizeInput();
        state.pendingAttachments = [];
        renderAttachmentStrip();
      }, 0);
    }
  },

  sessionBranched(d) {
    toast("Branched — you're now on the copy.");
    openSession(d.session.id);
  },

  sessionImported(d) { toast("Chat imported."); openSession(d.sessionId); },

  sessionDeleted(d) {
    if (d.sessionId === state.currentSessionId) {
      state.currentSessionId = null;
      state.currentSession = null;
      state.messages = [];
      clearMessages();
      el.chatTitle.textContent = "New chat";
      el.emptyState.classList.remove("hidden");
    }
  },

  sessionLoaded(d) {
    state.currentSession = d.session;
    state.messages = d.messages || [];
    el.chatTitle.textContent = d.session.title;
    el.providerSelect.value = d.session.provider || el.providerSelect.value;
    const prov = el.providerSelect.value;
    if (!state.modelsByProvider[prov]) bridge.send("listModels", { provider: prov });
    renderModelSelect(prov, d.session.model);
    updateThinkingSelect();
    renderAllMessages();
    renderSessions();
    updateCostLine();
  },

  userMessageSaved(d) {
    if (d.sessionId !== state.currentSessionId) return;
    state.messages.push(d.message);
    el.emptyState.classList.add("hidden");
    el.messages.appendChild(messageElement(d.message));
    scrollToBottom(true);
  },

  // Edit/regenerate rewrote history on the host. Trim the local copy in place
  // (a full reload would race with the streamStart that follows immediately).
  historyTruncated(d) {
    if (d.sessionId !== state.currentSessionId) return;
    const idx = state.messages.findIndex((m) => m.id === d.fromMessageId);
    if (idx === -1) return;

    const inclusive = d.editedText === undefined || d.editedText === null;
    const cut = inclusive ? idx : idx + 1;

    state.messages.slice(cut).forEach((m) => {
      const node = anchorMap.get(m.id);
      if (node) { io.unobserve(node); node.remove(); anchorMap.delete(m.id); }
    });
    state.messages = state.messages.slice(0, cut);

    if (!inclusive) {
      const edited = state.messages[idx];
      edited.content = d.editedText;
      const node = anchorMap.get(edited.id);
      if (node) node.querySelector(".bubble").textContent = d.editedText;
    }
    purgeCodeStore();
  },

  streamStart(d) {
    if (d.sessionId !== state.currentSessionId) return;
    setStreaming(true);
    state.streamMessageId = d.messageId;
    state.streamRaw = "";
    state.streamThinking = "";
    const placeholder = {
      id: d.messageId, role: "assistant", content: "", model: d.model,
      createdAt: new Date().toISOString(), attachments: [],
    };
    const elx = messageElement(placeholder);
    elx.querySelector(".bubble").innerHTML = '<span class="typing-dots"></span>';
    el.messages.appendChild(elx);
    el.emptyState.classList.add("hidden");
    scrollToBottom();
  },

  delta(d) {
    if (d.sessionId !== state.currentSessionId || d.messageId !== state.streamMessageId) return;
    state.streamRaw += d.text || "";
    queueStreamRender();
  },

  thinkingDelta(d) {
    if (d.sessionId !== state.currentSessionId || d.messageId !== state.streamMessageId) return;
    state.streamThinking += d.text || "";
    queueStreamRender();
  },

  toolCall(d) {
    if (d.toolName) toast(`Model requested tool "${d.toolName}" — tool execution isn't enabled yet.`);
  },

  streamDone(d) {
    setStreaming(false);
    if (d.sessionId !== state.currentSessionId) return;

    const wrap = anchorMap.get(state.streamMessageId);
    state.streamMessageId = null;

    const hasBody = d.message && (d.message.content || d.message.thinking);
    if (wrap && hasBody) {
      anchorMap.delete(wrap.dataset.messageId);
      const finalEl = messageElement(d.message);
      wrap.replaceWith(finalEl);
      state.messages.push(d.message);

      if (d.usage) {
        const u = d.usage;
        state.sessionTokens += (u.inputTokens || 0) + (u.outputTokens || 0);
        state.sessionCostUsd += u.costUsd || 0;
        const approx = u.estimated ? "≈" : "";
        finalEl.querySelector(".usage-line").textContent =
          `${approx}${u.inputTokens} in / ${approx}${u.outputTokens} out tokens` +
          (u.costUsd ? ` · ${approx}$${u.costUsd.toFixed(4)}` : "") +
          (d.cancelled ? " · stopped" : "");
        updateCostLine();
      }
      scrollToBottom();
    } else if (wrap) {
      wrap.remove(); // cancelled before any content arrived
    }
    purgeCodeStore();
  },

  streamError(d) {
    setStreaming(false);
    const wrap = anchorMap.get(state.streamMessageId);
    state.streamMessageId = null;
    if (wrap && !state.streamRaw) wrap.remove();
    toast(d.message || "Generation failed.", "error", 7000);
    if (d.sessionId === state.currentSessionId) bridge.send("loadSession", { sessionId: d.sessionId });
  },

  retrying(d) {
    toast(`Provider busy (${d.reason || "rate limited"}). Retrying in ${d.delaySeconds}s — attempt ${d.attempt}…`);
  },

  runStart(d) {
    const r = runs.get(d.runId);
    if (r) r.out.querySelector(".run-status").firstChild.textContent = "Running… ";
  },

  runOutput(d) {
    const r = runs.get(d.runId);
    if (r) appendRunLine(r.out, d.text, d.stderr);
  },

  runDone(d) {
    const r = runs.get(d.runId);
    runs.delete(d.runId);
    if (!r) return;
    const status = r.out.querySelector(".run-status");
    if (d.error) {
      status.textContent = d.error;
      status.classList.add("stderr");
      toast(d.error, "error");
    } else if (d.exitCode === null || d.exitCode === undefined) {
      status.textContent = "Stopped.";
    } else {
      status.textContent = `Exited with code ${d.exitCode}.`;
    }
  },

  models(d) {
    state.modelsByProvider[d.provider] = d.models || [];
    if (el.providerSelect.value === d.provider) {
      renderModelSelect(d.provider, state.currentSession?.model);
      updateThinkingSelect();
    }
  },

  searchResults(d) { renderSearchResults(d.hits || []); },

  settings(d) {
    state.settings = { ...state.settings, ...d };
    applySettings();
    renderRunCommandRows();
    updateThinkingSelect();
  },

  templates(d) { state.templates = d.templates || []; renderTemplateSelect(); toast("Templates updated."); },

  apiKeySaved(d) {
    const p = state.providers.find((x) => x.name === d.provider);
    if (p) p.hasApiKey = d.hasApiKey;
    renderApiKeyRows();
    renderProviderSelect();
    if (d.hasApiKey) bridge.send("listModels", { provider: d.provider });
    toast(d.hasApiKey ? `${d.provider} key saved (encrypted).` : `${d.provider} key removed.`);
  },

  exported(d) { toast(`Exported as ${d.format}.`); },
  fileSaved(d) { toast(`Saved ${d.fileName}.`); },
  systemPromptSaved() { if (state.currentSessionId) bridge.send("loadSession", { sessionId: state.currentSessionId }); },

  attachmentsPicked(d) {
    (d.attachments || []).forEach((a) => state.pendingAttachments.push(a));
    renderAttachmentStrip();
  },

  error(d) { toast(d.message || "Something went wrong.", "error", 7000); },
});

function updateCostLine() {
  el.sessionCost.textContent = state.sessionTokens
    ? `session: ${state.sessionTokens.toLocaleString()} tokens · ~$${state.sessionCostUsd.toFixed(4)}`
    : "";
}

// External links open in the default browser via the host.
document.addEventListener("click", (e) => {
  const a = e.target.closest("a[href]");
  if (a && /^https?:/i.test(a.href)) {
    e.preventDefault();
    bridge.send("openExternal", { url: a.href });
  }
});

// ------------------------------------------------------------------ Boot

autoSizeInput();
bridge.send("init");
