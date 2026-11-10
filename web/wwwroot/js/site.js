(() => {
  "use strict";

  const dateTimeFormatter = new Intl.DateTimeFormat("en-GB", {
    day: "numeric", month: "short", year: "numeric",
    hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23"
  });
  function formatDateTime(value) {
    const parts = Object.fromEntries(dateTimeFormatter.formatToParts(new Date(value))
      .filter(part => part.type !== "literal").map(part => [part.type, part.value]));
    return `${parts.day} ${parts.month} ${parts.year}, ${parts.hour}:${parts.minute}:${parts.second}`;
  }
  function storageGet(key) { try { return window.sessionStorage.getItem(key); } catch { return null; } }
  function storageSet(key, value) { try { window.sessionStorage.setItem(key, value); } catch { } }

  function localTimes(root) {
    root.querySelectorAll("time[data-local-time]").forEach(time => {
      const value = Date.parse(time.dateTime);
      if (!Number.isNaN(value)) time.textContent = formatDateTime(value);
    });
  }

  function tabs(root) {
    const containers = root.matches?.("[data-tabs-key]") ? [root] : root.querySelectorAll("[data-tabs-key]");
    containers.forEach(container => {
      const buttons = [...container.querySelectorAll('[role="tab"]')];
      if (!buttons.length) return;
      const key = container.dataset.tabsKey;
      const show = (button, focus = false) => {
        storageSet(key, button.id);
        buttons.forEach(item => {
          const selected = item === button;
          item.setAttribute("aria-selected", selected ? "true" : "false");
          item.tabIndex = selected ? 0 : -1;
          const panel = document.getElementById(item.getAttribute("aria-controls"));
          if (panel) panel.hidden = !selected;
        });
        if (focus) button.focus();
      };
      container.showTab = (name, focus) => { const button = buttons.find(item => item.id === `${name}-tab`); if (button) show(button, focus); };
      buttons.forEach((button, index) => {
        button.addEventListener("click", () => show(button));
        button.addEventListener("keydown", event => {
          const next = event.key === "ArrowRight" ? index + 1 : event.key === "ArrowLeft" ? index - 1 :
            event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1 : null;
          if (next === null) return;
          event.preventDefault();
          show(buttons[(next + buttons.length) % buttons.length], true);
        });
      });
      show(buttons.find(item => item.id === storageGet(key)) || buttons[0]);
    });
  }

  const pages = new Map();
  function paginate(root) {
    root.querySelectorAll("[data-paginate]").forEach(list => {
      const size = Number(list.dataset.paginate) || 10;
      const items = [...list.children];
      const pageCount = Math.ceil(items.length / size);
      if (pageCount < 2) return;
      const key = `${list.dataset.paginateLabel}:${items[0].querySelector("input")?.id || ""}`;
      const nav = document.createElement("nav"); nav.className = "cataloguePagination";
      nav.setAttribute("aria-label", list.dataset.paginateLabel || "Pages");
      const previous = button("Previous", () => go(-1));
      const next = button("Next", () => go(1));
      const label = document.createElement("span"); label.setAttribute("aria-live", "polite");
      nav.append(previous, label, next);
      list.after(nav);
      function go(step) { pages.set(key, (pages.get(key) || 0) + step); render(); }
      function render() {
        const current = Math.min(Math.max(pages.get(key) || 0, 0), pageCount - 1);
        pages.set(key, current);
        items.forEach((item, index) => { item.hidden = Math.floor(index / size) !== current; });
        label.textContent = `Page ${current + 1} of ${pageCount}`;
        previous.disabled = current === 0;
        next.disabled = current >= pageCount - 1;
      }
      render();
    });
  }

  function selections(root) {
    root.querySelectorAll("form[data-selection]").forEach(form => {
      const boxes = [...form.querySelectorAll('input[type="checkbox"]')];
      const submit = form.querySelector("[data-selection-label]");
      const locked = submit?.disabled;
      const sync = () => {
        const count = boxes.filter(box => box.checked).length;
        if (!submit) return;
        submit.textContent = submit.dataset.selectionLabel.replace("{0}", count);
        submit.disabled = locked || count === 0;
      };
      form.querySelectorAll("[data-select]").forEach(control => control.addEventListener("click", () => {
        boxes.forEach(box => { box.checked = control.dataset.select === "all"; });
        sync();
      }));
      boxes.forEach(box => box.addEventListener("change", sync));
      sync();
    });
  }

  let selectedRun = null;
  function testRuns(root) {
    root.querySelectorAll("[data-test-runs]").forEach(container => {
      const rows = [...container.querySelectorAll("[data-run-key]")];
      const details = [...container.querySelectorAll("[data-run-detail]")];
      const show = key => {
        selectedRun = key;
        rows.forEach(row => row.setAttribute("aria-pressed", row.dataset.runKey === key ? "true" : "false"));
        details.forEach(detail => { detail.hidden = detail.dataset.runDetail !== key; });
      };
      rows.forEach(row => row.addEventListener("click", () => show(row.dataset.runKey)));
      show(rows.some(row => row.dataset.runKey === selectedRun) ? selectedRun : rows[0].dataset.runKey);
    });
  }

  function readiness(root) {
    const panel = root.matches?.("[data-ready]") ? root : root.querySelector("[data-ready]");
    if (!panel) return;
    const ready = panel.dataset.ready === "true";
    document.querySelectorAll("[data-requires-ready]").forEach(control => { control.disabled = !ready; });
    document.querySelectorAll("[data-offline-note]").forEach(note => { note.hidden = ready; });
  }

  function busy(root) {
    const region = root.matches?.("[data-busy]") ? root : root.querySelector("[data-busy]");
    if (!region) return;
    const inert = region.querySelector("#discovery-running-panel") !== null;
    document.querySelectorAll(".sidebar, [data-inert-while-busy]").forEach(node => { node.inert = inert; });
  }

  function enhance(root) {
    localTimes(root); tabs(root); paginate(root); selections(root); testRuns(root); readiness(root); busy(root);
  }

  const timers = new Map();
  const streams = new Map();
  let submitting = false;

  function schedule(id) {
    window.clearTimeout(timers.get(id));
    const region = document.getElementById(id);
    if (!region?.dataset.live) return;
    timers.set(id, window.setTimeout(() => refresh(id), Number(region.dataset.liveInterval) || 10000));
  }

  async function refresh(id) {
    const region = document.getElementById(id);
    if (!region?.dataset.live) return;
    if (submitting || document.hidden) { schedule(id); return; }
    const note = document.querySelector("[data-live-note]");
    try {
      const response = await window.fetch(region.dataset.live, { cache: "no-store", credentials: "same-origin" });
      if (response.redirected) { window.location.reload(); return; }
      if (!response.ok) throw new Error(`Refresh failed (${response.status})`);
      const template = document.createElement("template");
      template.innerHTML = (await response.text()).trim();
      const next = template.content.firstElementChild;
      if (note) note.hidden = true;
      if (next && !submitting && document.getElementById(id) === region) replace(region, next);
    } catch {
      if (note) note.hidden = false;
    } finally { schedule(id); }
  }

  function replace(region, next) {
    if (next.dataset.navigate) { window.location.assign(next.dataset.navigate); return; }
    if (region.dataset.version && region.dataset.version === next.dataset.version) return;
    const wasBusy = region.dataset.busy === "true";
    const state = [...region.querySelectorAll("input[id], textarea[id], select[id]")]
      .filter(control => control.type !== "hidden")
      .map(control => [control.id, control.type === "checkbox" || control.type === "radio" ? control.checked : control.value]);
    const open = [...region.querySelectorAll("details[open][id]")].map(details => details.id);
    const known = new Set([...region.querySelectorAll("[id]")].map(node => node.id));
    const focused = document.activeElement?.id && region.contains(document.activeElement) ? document.activeElement.id : null;
    region.replaceWith(next);
    for (const [id, value] of state) {
      const control = document.getElementById(id);
      if (!control || !next.contains(control)) continue;
      if (typeof value === "boolean") control.checked = value; else control.value = value;
    }
    open.forEach(id => { const details = document.getElementById(id); if (details && next.contains(details)) details.open = true; });
    enhance(next);
    connect(next);
    if (wasBusy && next.dataset.busy === "false" && next.dataset.finishedTab) {
      selectedRun = null;
      testRuns(next);
      const container = next.matches("[data-tabs-key]") ? next : next.querySelector("[data-tabs-key]");
      container?.showTab?.(next.dataset.finishedTab, true);
    } else {
      const autofocus = [...next.querySelectorAll("[data-autofocus]")].find(node => !known.has(node.id));
      if (autofocus) autofocus.focus();
      else if (focused) document.getElementById(focused)?.focus();
    }
  }

  function connect(region) {
    const id = region.id;
    const url = region.dataset.events;
    const current = streams.get(id);
    if (current?.url === url) return;
    current?.source.close();
    streams.delete(id);
    if (!url || typeof window.EventSource !== "function") return;
    const source = new EventSource(url);
    source.onmessage = () => { window.clearTimeout(timers.get(id)); refresh(id); };
    streams.set(id, { url, source });
  }

  document.querySelectorAll("[data-live]").forEach(region => {
    if (!region.id) return;
    connect(region);
    schedule(region.id);
  });
  document.addEventListener("visibilitychange", () => {
    if (document.hidden) return;
    document.querySelectorAll("[data-live]").forEach(region => { if (region.id) refresh(region.id); });
  });
  window.addEventListener("pagehide", () => {
    streams.forEach(({ source }) => source.close());
    timers.forEach(timer => window.clearTimeout(timer));
  });

  document.addEventListener("submit", event => {
    const form = event.target;
    if (form.dataset.confirm && !window.confirm(form.dataset.confirm)) { event.preventDefault(); return; }
    if (event.defaultPrevented) return;
    submitting = true;
    const submitter = event.submitter;
    if (submitter?.dataset.busyLabel) {
      window.setTimeout(() => { submitter.textContent = submitter.dataset.busyLabel; submitter.disabled = true; });
    }
  });
  window.addEventListener("pageshow", event => { if (event.persisted) submitting = false; });

  document.addEventListener("change", event => {
    if (event.target.matches?.("[data-autosubmit]")) event.target.form?.requestSubmit();
  });

  if (!navigator.clipboard) document.documentElement.dataset.noClipboard = "";

  document.addEventListener("click", event => {
    const copy = event.target.closest?.("[data-copy-target]");
    if (copy) copyText(copy);
    const close = event.target.closest?.("[data-close-details]");
    if (close) {
      const details = close.closest("details");
      if (details) { details.open = false; details.querySelector("summary")?.focus(); }
    }
  });

  async function copyText(control) {
    const target = document.getElementById(control.dataset.copyTarget);
    if (!target) return;
    const text = "value" in target && typeof target.value === "string" ? target.value : target.textContent;
    const label = control.dataset.copyLabel || control.textContent;
    control.dataset.copyLabel = label;
    try {
      await navigator.clipboard.writeText(text);
      control.textContent = "Copied";
    } catch {
      control.textContent = "Couldn't copy";
    }
    window.setTimeout(() => { control.textContent = label; }, 2000);
  }

  document.querySelectorAll("dialog[data-show-modal]").forEach(dialog => { if (!dialog.open) dialog.showModal(); });
  document.querySelectorAll("[data-select-on-load]").forEach(input => input.select());

  document.querySelectorAll("[data-agent-provider]").forEach(provider => {
    const binary = provider.form?.querySelector("[data-agent-binary]");
    if (!binary) return;
    let previous = provider.value;
    provider.addEventListener("change", () => {
      if (!binary.value.trim() || binary.value.trim() === previous) binary.value = provider.value;
      previous = provider.value;
    });
  });

  enhance(document);
  document.querySelector("[data-autofocus]")?.focus();

  function button(label, handler) {
    const node = document.createElement("button");
    node.type = "button"; node.className = "secondaryButton"; node.textContent = label;
    node.addEventListener("click", handler);
    return node;
  }
})();
