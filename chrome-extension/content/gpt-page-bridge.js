/**
 * MAIN world — ChatGPT file attach must run here (isolated content scripts cannot
 * reliably trigger composer uploads). Talks to gpt-send.js via window.postMessage.
 */
(function () {
  if (window.__iaPageBridgeInstalled) return;
  window.__iaPageBridgeInstalled = true;

  var IA_ATTACH = "IA_ATTACH_IMAGE";
  var IA_RESULT = "IA_ATTACH_IMAGE_RESULT";

  function wait(ms) {
    return new Promise(function (r) {
      setTimeout(r, ms);
    });
  }

  function base64ToBytes(b64) {
    var bin = atob(String(b64 || ""));
    var out = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i += 1) out[i] = bin.charCodeAt(i);
    return out;
  }

  function isVisible(el) {
    if (!el || !(el instanceof Element)) return false;
    var st = window.getComputedStyle(el);
    if (st.display === "none" || st.visibility === "hidden" || Number(st.opacity) === 0) return false;
    var r = el.getBoundingClientRect();
    return r.width > 2 && r.height > 2;
  }

  function composerSurface() {
    return document.querySelector('[data-composer-surface="true"]');
  }

  function findComposer() {
    var surface = composerSurface();
    if (surface) {
      var prefer =
        surface.querySelector('div#prompt-textarea[contenteditable="true"]') ||
        surface.querySelector("div.ProseMirror[contenteditable='true']") ||
        surface.querySelector('[contenteditable="true"][role="textbox"]');
      if (prefer && isVisible(prefer)) return prefer;
    }
    var byId = document.querySelector('div#prompt-textarea[contenteditable="true"]');
    if (byId && isVisible(byId)) return byId;
    var ce = document.querySelector("[contenteditable='true']");
    if (ce && isVisible(ce)) return ce;
    return null;
  }

  async function waitForComposer(maxMs) {
    var limit = Date.now() + (maxMs || 20000);
    while (Date.now() < limit) {
      var c = findComposer();
      if (c) return c;
      await wait(80);
    }
    return null;
  }

  function hasAttachment(root, fileName) {
    if (!(root instanceof HTMLElement)) return false;
    var hints = ['[data-testid*="attachment"]', '[data-testid*="file"]', 'button[aria-label*="Remove" i]'];
    for (var i = 0; i < hints.length; i += 1) {
      try {
        if (root.querySelector(hints[i])) return true;
      } catch (_e) {}
    }
    var imgs = root.querySelectorAll("img");
    for (var j = 0; j < imgs.length; j += 1) {
      var src = (imgs[j].getAttribute("src") || "").trim();
      if (src.indexOf("blob:") === 0 || src.indexOf("data:image") === 0) return true;
    }
    if (fileName && (root.innerText || "").indexOf(fileName) >= 0) return true;
    return false;
  }

  async function waitAttachment(root, ms, fileName) {
    var limit = Date.now() + (ms || 10000);
    while (Date.now() < limit) {
      if (hasAttachment(root, fileName)) return true;
      await wait(90);
    }
    return false;
  }

  function dropFile(el, file, partner) {
    if (!(el instanceof HTMLElement) || !file) return false;
    var dt = new DataTransfer();
    try {
      dt.items.add(file);
    } catch (_e) {
      return false;
    }
    var opts = { bubbles: true, composed: true, cancelable: true, dataTransfer: dt };
    try {
      el.focus({ preventScroll: true });
    } catch (_e2) {
      try {
        el.focus();
      } catch (_e3) {}
    }
    try {
      el.dispatchEvent(new DragEvent("dragenter", opts));
      el.dispatchEvent(new DragEvent("dragover", opts));
      el.dispatchEvent(new DragEvent("drop", opts));
      return true;
    } catch (_e4) {
      return false;
    } finally {
      dismissDropOverlay(el, partner);
    }
  }

  function dismissDropOverlay(primary, partner) {
    var emptyDt = new DataTransfer();
    var leaveOpts = { bubbles: true, composed: true, cancelable: false, dataTransfer: emptyDt };
    var nodes = [];
    if (primary instanceof HTMLElement) nodes.push(primary);
    if (partner instanceof HTMLElement && partner !== primary) nodes.push(partner);
    var surf = composerSurface();
    if (surf instanceof HTMLElement && nodes.indexOf(surf) < 0) nodes.push(surf);
    for (var i = 0; i < nodes.length; i += 1) {
      try {
        nodes[i].dispatchEvent(new DragEvent("dragleave", leaveOpts));
      } catch (_e) {}
      try {
        nodes[i].dispatchEvent(new DragEvent("dragexit", leaveOpts));
      } catch (_e2) {}
    }
    try {
      window.dispatchEvent(new DragEvent("dragend", leaveOpts));
    } catch (_e3) {}
  }

  /** Extra empty drag cycle after attach — clears ChatGPT's drop overlay. */
  function clearDropOverlay(el, partner) {
    if (!(el instanceof HTMLElement)) return;
    var emptyDt = new DataTransfer();
    var opts = { bubbles: true, composed: true, cancelable: true, dataTransfer: emptyDt };
    try {
      el.dispatchEvent(new DragEvent("dragenter", opts));
      el.dispatchEvent(new DragEvent("dragover", opts));
      el.dispatchEvent(new DragEvent("dragleave", opts));
    } catch (_e) {}
    dismissDropOverlay(el, partner);
  }

  function pasteFile(composer, file) {
    if (!(composer instanceof HTMLElement) || !file) return false;
    try {
      var dt = new DataTransfer();
      dt.items.add(file);
      return composer.dispatchEvent(
        new ClipboardEvent("paste", { bubbles: true, cancelable: true, clipboardData: dt })
      );
    } catch (_e) {
      return false;
    }
  }

  function clickAttachMenu() {
    var btn =
      document.querySelector('button[data-testid="composer-plus-btn"]') ||
      document.querySelector('button[aria-label*="Attach" i]') ||
      document.querySelector('button[aria-label*="Upload" i]');
    if (btn instanceof HTMLElement && isVisible(btn)) {
      btn.click();
      return true;
    }
    return false;
  }

  function clickUploadMenuItem() {
    var candidates = document.querySelectorAll('[role="menuitem"], [role="option"], button[type="button"]');
    for (var i = 0; i < candidates.length; i += 1) {
      var el = candidates[i];
      if (!(el instanceof HTMLElement) || !isVisible(el)) continue;
      var t = (el.textContent || "").replace(/\s+/g, " ").trim();
      if (!t || t.length > 120) continue;
      if (/connect|drive|dropbox|onedrive|link only|paste url|microphone(?!.*file)/i.test(t)) continue;
      if (/\bupload\b|\bfile\b|\bphoto\b|\bimage\b|\battach\b|\bcomputer\b|\bbrowse\b|\bdocument\b/i.test(t)) {
        try {
          el.click();
          return true;
        } catch (_e) {}
      }
    }
    return false;
  }

  function fillFileInput(file) {
    var inputs = Array.prototype.slice.call(document.querySelectorAll('input[type="file"]'), 0);
    for (var i = 0; i < inputs.length; i += 1) {
      var input = inputs[i];
      if (!(input instanceof HTMLInputElement)) continue;
      try {
        var dt = new DataTransfer();
        dt.items.add(file);
        input.files = dt.files;
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
        return true;
      } catch (_e) {}
    }
    return false;
  }

  async function attachPngBase64(b64) {
    var trimmed = String(b64 || "").trim();
    if (!trimmed) return { ok: false, error: "image_missing" };

    var bytes;
    try {
      bytes = base64ToBytes(trimmed);
    } catch (_e) {
      return { ok: false, error: "image_missing" };
    }

    var fileName = "ia-sharex-" + Date.now() + ".png";
    var file = new File([bytes], fileName, { type: "image/png" });

    var composer = await waitForComposer(25000);
    if (!composer) return { ok: false, error: "composer_not_found" };

    var surf = composerSurface();
    var attachRoot = surf instanceof HTMLElement ? surf : composer;

    try {
      composer.focus({ preventScroll: true });
    } catch (_e) {
      try {
        composer.focus();
      } catch (_e2) {}
    }
    if (surf instanceof HTMLElement) {
      try {
        surf.click();
      } catch (_e3) {}
    }
    await wait(180);

    var ok = false;

    dropFile(attachRoot, file, composer);
    await wait(250);
    ok = await waitAttachment(attachRoot, 7000, fileName);

    if (!ok && composer !== attachRoot) {
      dropFile(composer, file, attachRoot);
      await wait(250);
      ok = await waitAttachment(attachRoot, 5000, fileName);
    }

    if (!ok) {
      pasteFile(composer, file);
      await wait(200);
      ok = await waitAttachment(attachRoot, 4000, fileName);
    }

    if (!ok && clickAttachMenu()) {
      await wait(350);
      clickUploadMenuItem();
      await wait(280);
      if (fillFileInput(file)) {
        await wait(500);
        ok = await waitAttachment(attachRoot, 10000, fileName);
      }
    }

    if (ok || hasAttachment(attachRoot, fileName)) {
      await wait(120);
      clearDropOverlay(attachRoot, composer);
      return { ok: true, phase: "attached" };
    }
    return { ok: false, error: "attachment_not_visible" };
  }

  window.addEventListener("message", function (event) {
    if (event.source !== window || !event.data || event.data.type !== IA_ATTACH) return;
    var requestId = event.data.requestId || "";
    var b64 = event.data.imagePngBase64 || "";

    attachPngBase64(b64)
      .then(function (result) {
        window.postMessage(
          { type: IA_RESULT, requestId: requestId, result: result || { ok: false, error: "empty_result" } },
          "*"
        );
      })
      .catch(function (e) {
        window.postMessage(
          {
            type: IA_RESULT,
            requestId: requestId,
            result: { ok: false, error: String((e && e.message) || e) },
          },
          "*"
        );
      });
  });
})();
