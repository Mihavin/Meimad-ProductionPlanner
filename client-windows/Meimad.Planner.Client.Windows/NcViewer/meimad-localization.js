"use strict";

// Meimad Planner localization of the NC viewer page. The page runs in WebView2, outside the WPF
// tree that LocalizationBehavior translates, so it translates its own interface text: exact
// catalog entries immediately, and composed messages through the host's translation engine.
// The NC program (CodeMirror), tables of data and form values are never touched, and the
// vendored viewer files stay unmodified because the text is replaced in the live DOM.
(() => {
  const meimad = window.fanucDesktop && window.fanucDesktop.meimad;
  if (!meimad || !meimad.localization) return;

  // Subtrees whose text is data, code or user input rather than interface text.
  const SKIP = ".CodeMirror, [translate='no'], script, style, textarea, input, canvas, pre, code, td";
  const ATTRIBUTES = ["title", "placeholder", "aria-label"];
  const HAS_WORD = /[A-Za-z]{2}/;

  let language = "en";
  let rightToLeft = false;
  let entries = new Map();
  const cache = new Map();          // English text -> translation, or null when there is none
  const sourceText = new WeakMap(); // text node -> English text it last received from the page
  const shownText = new WeakMap();  // text node -> text this script wrote
  const sourceAttributes = new WeakMap(); // element -> Map(attribute -> English value)
  const shownAttributes = new WeakMap();  // element -> Map(attribute -> value this script wrote)
  const waiting = new Map();        // English text -> Set of callbacks
  let flushTimer = 0;
  let observer = null;

  const normalize = (text) => text.trim().replace(/\s+/g, " ");
  const HEBREW = /[\u0590-\u05FF]/;

  // The page keeps a left-to-right layout (code, toolpath, numbers); a Hebrew phrase is shown
  // as a right-to-left isolate so its words and embedded names read in the right order.
  const display = (value) => (rightToLeft && HEBREW.test(value) ? "\u2067" + value + "\u2069" : value);

  function withOuterSpace(original, translated) {
    const leading = original.match(/^\s*/)[0];
    const trailing = original.match(/\s*$/)[0];
    return leading + translated + trailing;
  }

  // Catalog keys with {placeholders} become anchored patterns, indexed by their first letter so
  // frequently changing texts ("Ln 12, Col 4", playback time) never need a host round trip.
  let templates = null;
  const PLACEHOLDER = /\{[^{}]+\}/g;
  const escapeRegex = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

  function buildTemplates() {
    const prefixed = new Map();
    const prefixless = [];
    for (const [key, translation] of entries) {
      const holes = key.match(PLACEHOLDER);
      if (!holes || !holes.every((hole) => translation.includes(hole))) continue;
      const literals = key.split(PLACEHOLDER);
      const letters = literals.join("").replace(/[^A-Za-z]/g, "").length;
      if (!literals[0] && letters < 4) continue;
      let pattern = "^";
      literals.forEach((literal, index) => {
        pattern += escapeRegex(literal);
        if (index < holes.length) pattern += index === holes.length - 1 ? "(.+)" : "(.+?)";
      });
      const template = { prefix: literals[0], length: key.length, regex: new RegExp(pattern + "$", "s"), holes, translation };
      if (!template.prefix) {
        prefixless.push(template);
      } else {
        if (!prefixed.has(template.prefix[0])) prefixed.set(template.prefix[0], []);
        prefixed.get(template.prefix[0]).push(template);
      }
    }
    const order = (left, right) => right.prefix.length - left.prefix.length || right.length - left.length;
    for (const list of prefixed.values()) list.sort(order);
    prefixless.sort(order);
    return { prefixed, prefixless };
  }

  function translateTemplate(text) {
    const core = normalize(text);
    if (!core) return undefined;
    templates = templates || buildTemplates();
    for (const list of [templates.prefixed.get(core[0]) || [], templates.prefixless]) {
      for (const template of list) {
        if (template.prefix && !core.startsWith(template.prefix)) continue;
        const match = template.regex.exec(core);
        if (!match) continue;
        const translated = template.translation.replace(PLACEHOLDER, (hole) => {
          const index = template.holes.indexOf(hole);
          return index >= 0 ? match[index + 1] : hole;
        });
        return withOuterSpace(text, translated);
      }
    }
    return undefined;
  }

  // A translation, null when the text has none, or undefined when the host must be asked.
  function lookup(text) {
    const exact = entries.get(normalize(text));
    if (exact !== undefined) return withOuterSpace(text, exact);
    if (cache.has(text)) return cache.get(text);
    const templated = translateTemplate(text);
    if (templated !== undefined) {
      cache.set(text, templated);
      return templated;
    }
    return undefined;
  }

  function request(text, apply) {
    if (!waiting.has(text)) waiting.set(text, new Set());
    waiting.get(text).add(apply);
    if (!flushTimer) flushTimer = setTimeout(flush, 30);
  }

  async function flush() {
    flushTimer = 0;
    const texts = [...waiting.keys()];
    if (texts.length === 0) return;
    const callbacks = new Map(texts.map((text) => [text, waiting.get(text)]));
    texts.forEach((text) => waiting.delete(text));
    let results = [];
    try {
      results = await meimad.translate(texts);
    } catch (error) {
      console.error(error);
    }
    texts.forEach((text, index) => {
      const translated = typeof results[index] === "string" && results[index] !== text ? results[index] : null;
      cache.set(text, translated);
      for (const apply of callbacks.get(text)) apply(translated);
    });
  }

  function skipped(element) {
    return !element || element.closest(SKIP) !== null;
  }

  function translateTextNode(node) {
    if (!node.parentElement || skipped(node.parentElement)) return;
    const current = node.nodeValue || "";
    if (shownText.get(node) === current) return;
    sourceText.set(node, current);
    if (language === "en" || !HAS_WORD.test(current)) return;
    const translated = lookup(current);
    const apply = (value) => {
      if (value === null || sourceText.get(node) !== current || node.nodeValue !== current) return;
      const shown = display(value);
      shownText.set(node, shown);
      node.nodeValue = shown;
    };
    if (translated === undefined) request(current, apply);
    else apply(translated);
  }

  function translateAttribute(element, name) {
    if (skipped(element) && !element.matches("input, textarea")) return;
    const current = element.getAttribute(name);
    if (current === null) return;
    const shown = shownAttributes.get(element);
    if (shown && shown.get(name) === current) return;
    if (!sourceAttributes.has(element)) sourceAttributes.set(element, new Map());
    sourceAttributes.get(element).set(name, current);
    if (language === "en" || !HAS_WORD.test(current)) return;
    const translated = lookup(current);
    const apply = (value) => {
      if (value === null || element.getAttribute(name) !== current) return;
      if (!shownAttributes.has(element)) shownAttributes.set(element, new Map());
      const shown = display(value);
      shownAttributes.get(element).set(name, shown);
      element.setAttribute(name, shown);
    };
    if (translated === undefined) request(current, apply);
    else apply(translated);
  }

  function translateTree(root) {
    if (root.nodeType === Node.TEXT_NODE) {
      translateTextNode(root);
      return;
    }
    if (root.nodeType !== Node.ELEMENT_NODE) return;
    for (const name of ATTRIBUTES) {
      if (root.hasAttribute(name)) translateAttribute(root, name);
    }
    if (skipped(root)) return;
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
    let node = walker.nextNode();
    while (node) {
      if (node.nodeType === Node.TEXT_NODE) {
        translateTextNode(node);
      } else {
        for (const name of ATTRIBUTES) {
          if (node.hasAttribute(name)) translateAttribute(node, name);
        }
      }
      node = walker.nextNode();
    }
  }

  // Put every translated text back to its English source before another language is applied.
  function restoreSources(root) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
    let node = walker.currentNode;
    while (node) {
      if (node.nodeType === Node.TEXT_NODE) {
        const source = sourceText.get(node);
        if (source !== undefined && node.nodeValue === shownText.get(node)) {
          shownText.delete(node);
          node.nodeValue = source;
        }
      } else if (node.nodeType === Node.ELEMENT_NODE) {
        const sources = sourceAttributes.get(node);
        const shown = shownAttributes.get(node);
        if (sources && shown) {
          for (const [name, value] of shown) {
            if (node.getAttribute(name) === value) node.setAttribute(name, sources.get(name));
          }
          shownAttributes.delete(node);
        }
      }
      node = walker.nextNode();
    }
  }

  function apply(payload) {
    if (!payload) return;
    observer?.disconnect();
    restoreSources(document.body);
    language = payload.language || "en";
    rightToLeft = Boolean(payload.rightToLeft);
    entries = new Map(Object.entries(payload.entries || {}));
    templates = null;
    cache.clear();
    document.documentElement.lang = language;
    document.body.classList.toggle("meimad-rtl", Boolean(payload.rightToLeft));
    translateTree(document.body);
    observe();
  }

  function observe() {
    observer = new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (mutation.type === "characterData") {
          translateTextNode(mutation.target);
        } else if (mutation.type === "attributes") {
          translateAttribute(mutation.target, mutation.attributeName);
        } else {
          mutation.addedNodes.forEach(translateTree);
        }
      }
    });
    observer.observe(document.body, {
      subtree: true,
      childList: true,
      characterData: true,
      attributes: true,
      attributeFilter: ATTRIBUTES
    });
  }

  async function start() {
    try {
      apply(await meimad.localization());
    } catch (error) {
      console.error(error);
    }
    meimad.onLocalization(apply);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
