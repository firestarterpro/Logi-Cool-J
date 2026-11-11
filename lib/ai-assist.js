/* ai-assist.js: the AI box shared by the v1 builder and the v2 keys editor.
   Two paths:
   1. Always works: build a prompt, copy it into any chat, paste the answer back.
   2. Optional, off by default: send the prompt to a local endpoint, either
      Ollama (http://localhost:11434/api/generate or /api/chat) or any
      OpenAI-compatible /chat/completions URL (the owner's OVMS server is
      http://localhost:8000/v3/chat/completions).
   Both paths end in parseButton(), which turns the answer into one key.
   No API keys: the endpoints are local and nothing here sends credentials. */
(function (root) {
  "use strict";

  const STEP_TYPES = ["key", "text", "wait", "url", "open", "run", "go_to_page"];

  /** "ollama-generate" | "ollama-chat" | "openai" for an endpoint URL. hint wins unless "auto". */
  function detectFormat(url, hint) {
    if (hint === "ollama") return /\/api\/chat\/?$/i.test(pathOf(url)) ? "ollama-chat" : "ollama-generate";
    if (hint === "openai") return "openai";
    const p = pathOf(url);
    if (/\/api\/chat\/?$/i.test(p)) return "ollama-chat";
    if (/\/api\/generate\/?$/i.test(p)) return "ollama-generate";
    if (/\/chat\/completions\/?$/i.test(p)) return "openai";
    if (portOf(url) === "11434") return "ollama-generate";
    return "openai";
  }

  function pathOf(url) { try { return new URL(url).pathname; } catch { return String(url || ""); } }
  function portOf(url) { try { return new URL(url).port; } catch { return ""; } }

  /** The URL to POST to. A bare Ollama base (http://localhost:11434) gets /api/generate. */
  function resolveUrl(url, format) {
    const u = String(url || "").trim();
    if (format === "ollama-generate" && /^https?:\/\/[^/]+\/?$/i.test(u)) return u.replace(/\/?$/, "/api/generate");
    return u;
  }

  function buildRequest(endpoint, model, prompt, hint) {
    const format = detectFormat(endpoint, hint);
    const url = resolveUrl(endpoint, format);
    let body;
    if (format === "ollama-generate") body = { model, prompt, stream: false };
    else if (format === "ollama-chat") body = { model, messages: [{ role: "user", content: prompt }], stream: false };
    else body = { model, messages: [{ role: "user", content: prompt }], stream: false, temperature: 0.2, max_tokens: 1024 };
    return { url, format, body };
  }

  function parseResponse(data, format) {
    if (!data || typeof data !== "object") return "";
    if (format === "ollama-generate") return String(data.response ?? "");
    if (format === "ollama-chat") return String(data.message?.content ?? "");
    const c = data.choices && data.choices[0];
    return String(c?.message?.content ?? c?.text ?? "");
  }

  /** Sends the prompt. Resolves to the answer text; rejects with a readable Error. */
  async function ask(opts) {
    const { endpoint, model, prompt } = opts;
    if (!endpoint) throw new Error("No endpoint set.");
    if (!/^https?:\/\//i.test(endpoint)) throw new Error("The endpoint must start with http:// or https://.");
    const req = buildRequest(endpoint, model || "", prompt, opts.format || "auto");
    const fetchImpl = opts.fetchImpl || root.fetch.bind(root);
    const ctrl = typeof AbortController !== "undefined" ? new AbortController() : null;
    const timer = ctrl ? setTimeout(() => ctrl.abort(), opts.timeoutMs || 90000) : null;
    let res;
    try {
      res = await fetchImpl(req.url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(req.body),
        signal: ctrl ? ctrl.signal : undefined
      });
    } catch (e) {
      throw new Error(e && e.name === "AbortError" ? "The endpoint took too long to answer." : "Could not reach " + req.url + " (server off, or it blocks pages opened from a file).");
    } finally { if (timer) clearTimeout(timer); }
    let data = null;
    try { data = await res.json(); } catch { /* not JSON */ }
    if (!res.ok) {
      const msg = data && (data.error?.message || data.error || data.message);
      throw new Error("Endpoint answered " + res.status + (msg ? ": " + msg : "."));
    }
    const text = parseResponse(data, req.format).trim();
    if (!text) throw new Error("The endpoint answered, but with no text.");
    return text;
  }

  /** The YAML part of an answer: drops <think> blocks and code fences, keeps the first fenced block if any. */
  function extractYaml(text) {
    let t = String(text || "").replace(/<think>[\s\S]*?<\/think>/gi, "").trim();
    const fence = t.match(/```[ \t]*(?:ya?ml)?[ \t]*\r?\n([\s\S]*?)```/i);
    if (fence) return fence[1].trim();
    // No fence: start at the first line that looks like YAML for a key.
    const lines = t.split(/\r?\n/);
    const start = lines.findIndex(l => /^\s*(-\s*)?(label|steps|button|id|color)\s*:/.test(l));
    return (start > 0 ? lines.slice(start) : lines).join("\n").trim();
  }

  /** Turns an answer into { label, color, steps: [{type, value}] }. Throws an Error listing every problem. */
  function parseButton(text, yamlLib) {
    const lib = yamlLib || root.jsyaml;
    if (!lib) throw new Error("js-yaml is not loaded.");
    let doc;
    try { doc = lib.load(extractYaml(text)); }
    catch (e) { throw new Error("The answer is not valid YAML: " + String(e.message || e).split("\n")[0]); }
    if (Array.isArray(doc)) doc = doc.length === 1 ? doc[0] : doc;
    if (doc && typeof doc === "object" && !Array.isArray(doc)) {
      if (doc.button && typeof doc.button === "object") doc = doc.button;
      else if (Array.isArray(doc.buttons) && doc.buttons.length === 1) doc = doc.buttons[0];
    }
    if (!doc || typeof doc !== "object" || Array.isArray(doc) || !Array.isArray(doc.steps)) {
      throw new Error("The answer needs one key with a steps: list.");
    }
    const problems = [];
    const steps = doc.steps.map((s, i) => {
      if (!s || typeof s !== "object" || Array.isArray(s)) { problems.push(`step ${i + 1} is not a mapping`); return null; }
      const keys = Object.keys(s);
      if (keys.length !== 1) { problems.push(`step ${i + 1} needs exactly one of ${STEP_TYPES.join(", ")}`); return null; }
      const type = keys[0];
      if (!STEP_TYPES.includes(type)) { problems.push(`step ${i + 1}: unknown step '${type}'`); return null; }
      const v = s[type];
      if (v === null || v === undefined || typeof v === "object") { problems.push(`step ${i + 1} (${type}) has no value`); return null; }
      if ((type === "wait" || type === "go_to_page") && !Number.isInteger(Number(v))) { problems.push(`step ${i + 1}: ${type} must be a whole number`); return null; }
      return { type, value: (type === "wait" || type === "go_to_page") ? Number(v) : String(v) };
    });
    if (steps.length > 1 && steps.some(s => s && s.type === "go_to_page")) problems.push("go_to_page must be the key's only step");
    if (problems.length) throw new Error("The answer has problems: " + problems.join("; ") + ".");
    const out = { label: doc.label == null ? "" : String(doc.label), steps };
    if (doc.color != null) out.color = String(doc.color);
    return out;
  }

  /** The prompt for one key. opts: { pageCount, paths: {name: path}, allowColor } */
  function buttonPrompt(request, opts) {
    const o = opts || {};
    const paths = o.paths && Object.keys(o.paths).length
      ? "\nPath variables you can use inside open/run/text values as ${name}:\n" +
        Object.entries(o.paths).map(([k, v]) => `  ${k}: ${v}`).join("\n") + "\n"
      : "";
    return `You are writing ONE key for a Logitech MX Creative Console keypad on Windows 11.
Answer with ONLY a YAML mapping inside a \`\`\`yaml code fence, no prose:
  label: short text (12 characters or fewer)${o.allowColor ? '\n  color: "#RRGGBB" (optional)' : ""}
  steps: a list run in order; each step has exactly one of these keys:
    - key: "Ctrl+Shift+T"   modifiers Ctrl, Shift, Alt, Win; keys A-Z, 0-9, F1-F24, Return, Tab, Space,
                            Escape, Backspace, Delete, Left, Right, Up, Down, \`, MediaPlayPause, VolumeMute
    - text: "typed text"    no Enter; add a key: "Return" step after it if needed
    - wait: 500             milliseconds, whole number
    - url: "https://..."
    - open: "C:\\\\path\\\\app.exe --args"   a program (with arguments), a folder or a file
    - run: "C:\\\\path\\\\script.bat"
    - go_to_page: 1         1 to ${o.pageCount || 10}; must be the only step
Never put passwords, API keys or tokens in any value.
Quote every string value with double quotes and double each backslash.
${paths}
KEY TO MAKE:
${request}`;
  }

  root.AiAssist = { STEP_TYPES, detectFormat, resolveUrl, buildRequest, parseResponse, ask, extractYaml, parseButton, buttonPrompt };
  if (typeof module !== "undefined" && module.exports) module.exports = root.AiAssist;
})(typeof window !== "undefined" ? window : globalThis);
