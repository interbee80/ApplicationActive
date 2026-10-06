'use strict';

/*
 * ApplicationActive — клиент Web-панели.
 * Работает только через REST API (см. docs/API.md), без внешних библиотек.
 * Данные всегда выводятся через textContent — HTML из ответов не подставляется.
 */
(function () {
  // --------------------------------------------------------------- помощники

  const $ = function (id) { return document.getElementById(id); };
  const KEY_STORAGE = 'appactive.key';
  const THEME_STORAGE = 'appactive.theme';
  const HOUR_MS = 3600000;
  const DAY_MS = 86400000;
  const TOP_APPS = 8;
  const DAY_NAMES = ['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'];
  const AUTO_REFRESH_MS = 30000;

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function svgNode(tag, attrs) {
    const node = document.createElementNS('http://www.w3.org/2000/svg', tag);
    if (attrs) {
      for (const key in attrs) {
        if (Object.prototype.hasOwnProperty.call(attrs, key) && attrs[key] !== undefined && attrs[key] !== null) {
          node.setAttribute(key, String(attrs[key]));
        }
      }
    }
    return node;
  }

  function clear(node) {
    if (!node) return;
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function pad2(value) { return String(value).padStart(2, '0'); }

  function localDayKey(date) {
    return date.getFullYear() + '-' + pad2(date.getMonth() + 1) + '-' + pad2(date.getDate());
  }

  function fmtDuration(seconds) {
    const total = Math.max(0, Math.round(Number(seconds) || 0));
    const h = Math.floor(total / 3600);
    const m = Math.floor((total % 3600) / 60);
    const s = total % 60;
    if (h > 0) return h + ' ч ' + pad2(m) + ' мин';
    if (m > 0) return s > 0 ? m + ' мин ' + s + ' с' : m + ' мин';
    return s + ' с';
  }

  function fmtAxis(seconds) {
    const total = Math.round(Number(seconds) || 0);
    if (total >= 3600) {
      const hours = total / 3600;
      return (hours % 1 === 0 ? hours.toFixed(0) : hours.toFixed(1)) + ' ч';
    }
    return Math.round(total / 60) + ' мин';
  }

  function fmtMinutes(seconds) { return Math.round((Number(seconds) || 0) / 60); }

  function fmtClock(iso) {
    const date = new Date(iso);
    if (isNaN(date.getTime())) return '—';
    return date.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  }

  function fmtDateTime(iso) {
    const date = new Date(iso);
    if (isNaN(date.getTime())) return '—';
    return date.toLocaleString('ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
  }

  function fmtPercent(value) {
    const num = Math.round((Number(value) || 0) * 10) / 10;
    return num.toLocaleString('ru-RU') + '%';
  }

  function fmtNumber(value) {
    return (Number(value) || 0).toLocaleString('ru-RU');
  }

  function fmtDateHuman(dayKey) {
    const date = new Date(dayKey + 'T00:00:00');
    if (isNaN(date.getTime())) return dayKey;
    return date.toLocaleDateString('ru-RU', { weekday: 'short', day: '2-digit', month: 'long', year: 'numeric' });
  }

  // Цвета приложений приходят со службы (dashboard.appColors), поэтому одно и то же
  // приложение одинаково окрашено в графике, легенде, ленте дня, таблице и чипах.
  const FALLBACK_PALETTE = [
    '#4f8cff', '#3fb27f', '#e0a33a', '#d95757', '#8a6fe8', '#2fa8c9',
    '#c95fa8', '#6fae3f', '#e07b39', '#5b7cc9', '#b45fd9', '#3fb7a4',
    '#c9a23f', '#d95f7b', '#7f8ce0', '#4fb07f', '#c96f3f', '#9b8ce0',
    '#3f9ec9', '#d9a05f', '#5fb0d9', '#a8c93f', '#e05f5f', '#5f8ad9'
  ];
  const OTHER_COLOR = '#8b93a7';

  // Признак того, что пользователь тянет выделение на графике (тогда подсказка не показывается).
  let chartDragging = false;

  function colorFor(appId) {
    if (appId === -1 || appId === null || appId === undefined) return OTHER_COLOR;
    const fromServer = state.colors && state.colors[String(appId)];
    if (fromServer) return fromServer;
    const numeric = Number(appId);
    const index = Number.isFinite(numeric) ? Math.abs(Math.trunc(numeric)) % FALLBACK_PALETTE.length : 0;
    return FALLBACK_PALETTE[index];
  }

  function severityClass(severity) {
    const value = String(severity || 'info').toLowerCase();
    if (value === 'critical') return 'badge badge-critical';
    if (value === 'warning') return 'badge badge-warning';
    return 'badge badge-info';
  }

  function severityText(severity) {
    const value = String(severity || 'info').toLowerCase();
    if (value === 'critical') return 'критично';
    if (value === 'warning') return 'предупреждение';
    return 'информация';
  }

  function daysText(days) {
    if (!days || !days.length) return 'каждый день';
    const names = days.slice().sort(function (a, b) { return a - b; })
      .map(function (d) { return DAY_NAMES[d - 1] || String(d); });
    return names.length === 7 ? 'каждый день' : names.join(', ');
  }

  function setMsg(node, text, kind) {
    if (!node) return;
    if (!text) { node.hidden = true; node.textContent = ''; node.className = 'msg'; return; }
    node.hidden = false;
    node.textContent = text;
    node.className = 'msg msg-' + (kind || 'info');
  }

  function toast(text, kind) {
    const host = $('toasts');
    if (!host) return;
    const box = el('div', 'toast toast-' + (kind || 'info'));
    box.appendChild(el('div', 'toast-text', text));
    const close = el('button', 'toast-close', '×');
    close.type = 'button';
    close.title = 'Закрыть';
    close.addEventListener('click', function () { box.remove(); });
    box.appendChild(close);
    host.appendChild(box);
    setTimeout(function () { if (box.parentNode) box.remove(); }, 7000);
  }

  // ------------------------------------------------------------------- API

  async function api(path, options) {
    const opts = Object.assign({ method: 'GET' }, options || {});
    opts.headers = Object.assign({}, opts.headers);
    if (state.key) opts.headers['X-AppActive-Key'] = state.key;
    if (opts.body !== undefined && typeof opts.body !== 'string') {
      opts.headers['Content-Type'] = 'application/json';
      opts.body = JSON.stringify(opts.body);
    }

    const response = await fetch(path, opts);
    const text = await response.text();
    let data = null;
    if (text) {
      try { data = JSON.parse(text); } catch (error) { data = null; }
    }
    if (!response.ok) {
      const message = (data && (data.error || data.title || data.detail)) || ('HTTP ' + response.status);
      const failure = new Error(message);
      failure.status = response.status;
      throw failure;
    }
    return data;
  }

  // --------------------------------------------------------------- состояние

  const state = {
    date: localDayKey(new Date()),
    key: localStorage.getItem(KEY_STORAGE) || '',
    requiresAdminKey: false,
    autoTimer: null,
    apps: [],
    groups: [],
    criticalRules: [],
    criticalStatus: [],
    workflowRules: [],
    dashboard: null,
    settings: null,
    hiddenApps: new Set(),
    colors: {},
    // Период: пустые значения — сутки state.date. Иначе — выражения вида now-1d / 2026-10-06T10:30.
    range: { from: '', to: '', label: '' },
    edit: { critical: null, group: null, workflow: null },
    warnings: []
  };

  function query(path) {
    const join = path.indexOf('?') >= 0 ? '&' : '?';
    let result = path + join + 'date=' + encodeURIComponent(state.date);
    if (state.range.from) result += '&from=' + encodeURIComponent(state.range.from);
    if (state.range.to) result += '&to=' + encodeURIComponent(state.range.to);
    return result;
  }

  // ------------------------------------------------------------- загрузка

  async function loadAll() {
    const tasks = [
      api('api/settings'),
      api(query('api/dashboard')),
      api(query('api/groups')),
      api('api/critical-rules'),
      api(query('api/critical/status')),
      api('api/workflow/rules'),
      api(query('api/workflow/events') + '&limit=100'),
      api(query('api/apps') + '&sort=todaySeconds'),
      api('api/health'),
      api('api/diagnostics')
    ];
    const names = ['settings', 'dashboard', 'groups', 'criticalRules', 'criticalStatus', 'workflowRules',
      'workflowEvents', 'apps', 'health', 'diagnostics'];

    const results = await Promise.allSettled(tasks);
    state.warnings = [];
    let hardFailures = 0;
    let fatal = false;

    results.forEach(function (result, index) {
      const name = names[index];
      if (result.status === 'fulfilled') {
        // Ошибка отрисовки одного блока не должна ломать остальные разделы панели.
        try {
          apply(name, result.value);
        } catch (renderError) {
          state.warnings.push(name + ': ошибка отрисовки — ' + describeError(renderError));
        }
      } else {
        state.warnings.push(name + ': ' + describeError(result.reason));
        if (name === 'settings' || name === 'dashboard' || name === 'health' || name === 'apps') hardFailures++;
        if (name === 'settings' && (result.reason.status === 401 || result.reason.status === 403)) fatal = true;
      }
    });

    setOffline(hardFailures >= 3 || fatal);
    renderWarnings();
    $('updated-label').textContent = 'обновлено в ' + new Date().toLocaleTimeString('ru-RU');
  }

  function describeError(error) {
    if (!error) return 'неизвестная ошибка';
    if (error.status === 401) return 'требуется ключ администратора (401)';
    if (error.status === 403) return 'доступ запрещён (403)';
    return error.message || String(error);
  }

  function apply(name, value) {
    switch (name) {
      case 'settings':
        state.settings = value;
        state.requiresAdminKey = !!value.requiresAdminKey;
        renderKeyField();
        renderSettings(value);
        break;
      case 'dashboard':
        state.dashboard = value;
        renderDashboard(value);
        break;
      case 'groups':
        state.groups = (value && value.groups) || [];
        renderGroups();
        renderGroupPicker();
        break;
      case 'criticalRules':
        state.criticalRules = (value && value.rules) || [];
        renderCriticalRules();
        renderCriticalTargets();
        break;
      case 'criticalStatus':
        state.criticalStatus = (value && value.rules) || [];
        renderCriticalStatus();
        break;
      case 'workflowRules':
        state.workflowRules = (value && value.rules) || [];
        renderWorkflowRules();
        break;
      case 'workflowEvents':
        renderWorkflowEvents((value && value.events) || []);
        break;
      case 'apps':
        state.apps = (value && value.apps) || [];
        renderApps();
        renderAppPicker();
        break;
      case 'health':
        renderHealth(value);
        break;
      case 'diagnostics':
        renderDiagnostics(value);
        break;
      default:
        break;
    }
  }

  function setOffline(offline, message) {
    const banner = $('offline-banner');
    if (!banner) return;
    banner.hidden = !offline;
    if (offline) {
      $('offline-text').textContent = message || (state.warnings.length ? state.warnings.join('; ') : 'Служба не отвечает.');
    }
  }

  function renderWarnings() {
    const banner = $('warning-banner');
    const list = $('warning-list');
    clear(list);
    if (!state.warnings.length || !$('offline-banner').hidden) {
      banner.hidden = true;
      return;
    }
    state.warnings.forEach(function (text) { list.appendChild(el('li', null, text)); });
    banner.hidden = false;
  }

  function renderKeyField() {
    const field = $('key-field');
    if (!field) return;
    field.hidden = !state.requiresAdminKey;
    if (state.requiresAdminKey) $('key-input').value = state.key;
  }

  // ------------------------------------------------------------ ОБЗОР: KPI

  function renderDashboard(data) {
    state.colors = data.appColors || {};
    const buckets = (data.buckets && data.buckets.length ? data.buckets : data.hours) || [];
    const bucketKind = data.range && data.range.bucketKind ? data.range.bucketKind : 'hour';

    renderKpis(data);
    renderChart(buckets, bucketKind);
    renderHoursTable(buckets);
    renderTimeline(data.timeline || [], data.topApps || [], data.range || null);
    renderUsers(data.perUser || []);
    renderEvents(data.events || []);
    renderAgentsInline(data.agents || []);
    renderPeriodBar(data.range || null);

    $('critical-date-label').textContent = fmtDateHuman(data.date || state.date);

    const settings = state.settings || data.settings || {};
    const parts = [];
    if (data.range && data.range.label) parts.push('период: ' + data.range.label);
    if (settings.pollIntervalSeconds) {
      parts.push('опрос ' + settings.pollIntervalSeconds + ' с, N = ' + settings.activityIdleSeconds + ' с');
    }
    $('chart-subtitle').textContent = parts.join(' · ');
  }

  // ------------------------------------------------------- выбор периода

  const QUICK_RANGES = [
    { label: '30 мин', from: 'now-30m', to: 'now' },
    { label: '1 ч', from: 'now-1h', to: 'now' },
    { label: '6 ч', from: 'now-6h', to: 'now' },
    { label: '24 ч', from: 'now-24h', to: 'now' },
    { label: '7 дн', from: 'now-7d', to: 'now' },
    { label: '30 дн', from: 'now-30d', to: 'now' },
    { label: 'Сегодня', from: 'today', to: 'now' },
    { label: 'Вчера', from: 'yesterday', to: 'today' }
  ];

  /// Строит строку выбора периода над графиком (один раз).
  function ensurePeriodBar() {
    if ($('period-bar')) return $('period-bar');

    const bar = el('div', 'period-bar');
    bar.id = 'period-bar';

    const quick = el('div', 'period-quick');
    QUICK_RANGES.forEach(function (preset) {
      const button = el('button', 'period-preset', preset.label);
      button.type = 'button';
      button.title = preset.from + ' … ' + preset.to;
      button.addEventListener('click', function () { setRange(preset.from, preset.to, preset.label); });
      quick.appendChild(button);
    });
    bar.appendChild(quick);

    const inputs = el('div', 'period-inputs');
    const fromField = el('label', 'field field-inline');
    fromField.appendChild(el('span', 'field-label', 'С'));
    const fromInput = el('input');
    fromInput.type = 'text';
    fromInput.id = 'range-from';
    fromInput.placeholder = 'now-1d или 2026-10-06T10:30';
    fromInput.size = 22;
    fromField.appendChild(fromInput);
    inputs.appendChild(fromField);

    const toField = el('label', 'field field-inline');
    toField.appendChild(el('span', 'field-label', 'По'));
    const toInput = el('input');
    toInput.type = 'text';
    toInput.id = 'range-to';
    toInput.placeholder = 'now или 2026-10-06T12:00';
    toInput.size = 22;
    toField.appendChild(toInput);
    inputs.appendChild(toField);

    const applyButton = el('button', 'btn btn-primary', 'Применить');
    applyButton.type = 'button';
    applyButton.id = 'range-apply';
    applyButton.addEventListener('click', function () {
      setRange($('range-from').value.trim(), $('range-to').value.trim(), 'вручную');
    });
    inputs.appendChild(applyButton);

    const resetButton = el('button', 'btn', 'Сутки');
    resetButton.type = 'button';
    resetButton.id = 'range-reset';
    resetButton.title = 'Вернуться к суткам выбранной даты';
    resetButton.addEventListener('click', function () { clearRange(); });
    inputs.appendChild(resetButton);

    [fromInput, toInput].forEach(function (input) {
      input.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') {
          event.preventDefault();
          setRange($('range-from').value.trim(), $('range-to').value.trim(), 'вручную');
        }
      });
    });

    bar.appendChild(inputs);

    const hint = el('div', 'period-hint',
      'Формат: now-1h, now-7d, now+30m, today, yesterday, 2026-10-06, 2026-10-06T10:30. ' +
      'Выделите участок на графике мышью, чтобы задать период.');
    bar.appendChild(hint);

    const chartCard = $('chart-host').closest('.card');
    const head = chartCard ? chartCard.querySelector('.card-head') : null;
    if (head && head.parentNode) {
      head.parentNode.insertBefore(bar, head.nextSibling);
    } else if (chartCard) {
      chartCard.insertBefore(bar, chartCard.firstChild);
    }

    return bar;
  }

  function renderPeriodBar(range) {
    const bar = ensurePeriodBar();
    const fromInput = $('range-from');
    const toInput = $('range-to');
    if (!fromInput || !toInput) return;

    if (document.activeElement !== fromInput && document.activeElement !== toInput) {
      fromInput.value = state.range.from || (range ? range.fromLocal.replace(' ', 'T') : '');
      toInput.value = state.range.to || (range ? range.toLocal.replace(' ', 'T') : '');
    }

    Array.prototype.forEach.call(bar.querySelectorAll('.period-preset'), function (button) {
      const title = button.title || '';
      button.classList.toggle('is-active', title === (state.range.from + ' … ' + state.range.to));
    });

    const badge = bar.querySelector('.period-active');
    if (badge) badge.remove();
    if (state.range.from || state.range.to) {
      const note = el('span', 'period-active muted',
        range && range.label ? 'период: ' + range.label : 'период: ' + state.range.from + ' … ' + state.range.to);
      bar.appendChild(note);
    }
  }

  /// Применяет период: обновляет состояние, ссылки и перезагружает данные.
  function setRange(from, to, note) {
    const hasFrom = !!(from && from.length);
    const hasTo = !!(to && to.length);
    if (!hasFrom && !hasTo) {
      clearRange();
      return;
    }

    state.range.from = hasFrom ? from : '';
    state.range.to = hasTo ? to : 'now';
    updateCsvLink();
    syncAddressBar();
    toast('Период: ' + (note || state.range.from + ' … ' + state.range.to), 'info');
    loadAll();
  }

  function clearRange() {
    state.range.from = '';
    state.range.to = '';
    updateCsvLink();
    syncAddressBar();
    loadAll();
  }

  function updateCsvLink() {
    const link = $('csv-link');
    if (!link) return;
    const params = ['date=' + encodeURIComponent(state.date)];
    if (state.range.from) params.push('from=' + encodeURIComponent(state.range.from));
    if (state.range.to) params.push('to=' + encodeURIComponent(state.range.to));
    link.href = 'api/export/csv?' + params.join('&');
    link.setAttribute('download', 'activity-' + state.date + '.csv');
  }

  /// Период и дату храним в адресе страницы: ссылку можно сохранить в закладки или передать коллеге.
  function syncAddressBar() {
    try {
      const params = new URLSearchParams();
      params.set('date', state.date);
      if (state.range.from) params.set('from', state.range.from);
      if (state.range.to) params.set('to', state.range.to);
      history.replaceState(null, '', location.pathname + '?' + params.toString());
    } catch (error) {
      // адресная строка недоступна — не критично
    }
  }

  /// Читает дату и период из адреса страницы (например, ?from=now-7d&to=now).
  function readAddressBar() {
    let params;
    try {
      params = new URLSearchParams(location.search);
    } catch (error) {
      return;
    }

    const date = params.get('date');
    if (date && /^\d{4}-\d{2}-\d{2}$/.test(date)) state.date = date;

    const from = (params.get('from') || '').trim();
    const to = (params.get('to') || '').trim();
    if (from || to) {
      state.range.from = from;
      state.range.to = to || 'now';
    }
  }

  function kpiCard(label, value, sub, modifier, targetTab) {
    const node = el(targetTab ? 'button' : 'div', 'kpi' + (modifier ? ' ' + modifier : ''));
    if (targetTab) node.type = 'button';
    node.appendChild(el('div', 'kpi-label', label));
    node.appendChild(el('div', 'kpi-value', value));
    if (sub) node.appendChild(el('div', 'kpi-sub', sub));
    if (targetTab) {
      node.title = 'Открыть раздел';
      node.addEventListener('click', function () { selectTab(targetTab); });
    }
    return node;
  }

  function renderKpis(data) {
    const host = $('kpi-cards');
    clear(host);
    const totals = data.totals || {};
    const settings = data.settings || state.settings || {};
    const agents = data.agents || [];
    const alive = agents.filter(function (a) { return a.alive; }).length;

    host.appendChild(kpiCard('Активное время за сутки', fmtDuration(totals.trackedSeconds), fmtDateHuman(data.date)));
    host.appendChild(kpiCard('Приложений с активностью', fmtNumber(totals.appCount),
      'всего в базе: ' + fmtNumber(totals.monitoredAppCount)));
    host.appendChild(kpiCard('Пользователей / сеансов', fmtNumber(totals.userCount) + ' / ' + fmtNumber(totals.sessionCount),
      alive ? 'агентов на связи: ' + alive : 'агенты не отчитывались'));
    host.appendChild(kpiCard('Превышено правил',
      fmtNumber(totals.criticalExceededCount),
      'правил за сутки: ' + fmtNumber(totals.criticalRuleCount),
      totals.criticalExceededCount > 0 ? 'kpi-bad' : 'kpi-ok',
      'critical'));
    host.appendChild(kpiCard('Служба', settings.monitorEnabled ? 'учёт включён' : 'учёт выключен',
      settings.pollIntervalSeconds ? 'интервал ' + settings.pollIntervalSeconds + ' с · окно N ' + settings.activityIdleSeconds + ' с' : '',
      settings.monitorEnabled ? '' : 'kpi-warn', 'diagnostics'));
  }

  // ------------------------------------------------------------ ОБЗОР: часы

  function topAppIds(hours) {
    const totals = new Map();
    hours.forEach(function (hour) {
      (hour.apps || []).forEach(function (app) {
        totals.set(app.appId, (totals.get(app.appId) || 0) + (Number(app.seconds) || 0));
      });
    });
    return Array.from(totals.entries())
      .sort(function (a, b) { return b[1] - a[1]; })
      .slice(0, TOP_APPS)
      .map(function (entry) { return entry[0]; });
  }

  function appLabel(hours, appId) {
    for (let i = 0; i < hours.length; i++) {
      const list = hours[i].apps || [];
      for (let j = 0; j < list.length; j++) {
        if (list[j].appId === appId) return list[j].displayName || list[j].exeName || ('#' + appId);
      }
    }
    return '#' + appId;
  }

  function renderChart(buckets, bucketKind) {
    const host = $('chart-host');
    const legend = $('chart-legend');
    const tooltip = $('chart-tooltip');
    clear(host);
    clear(legend);
    tooltip.hidden = true;

    const byDay = bucketKind === 'day';
    const totals = buckets.map(function (b) { return Number(b.totalSeconds) || 0; });
    const max = Math.max.apply(null, totals.concat([60]));
    if (!buckets.length || max <= 0) {
      host.appendChild(el('div', 'chart-empty', 'За выбранный период активности нет.'));
      return;
    }

    const topIds = topAppIds(buckets);
    const visible = function (appId) {
      return !state.hiddenApps.has(appId) && (topIds.indexOf(appId) >= 0 || appId === -1);
    };

    const width = 1200;
    const height = 320;
    const pad = { top: 14, right: 16, bottom: 30, left: 60 };
    const plotW = width - pad.left - pad.right;
    const plotH = height - pad.top - pad.bottom;
    const step = plotW / buckets.length;
    const barW = Math.max(1.5, step * 0.66);
    const labelEvery = Math.max(1, Math.ceil(buckets.length / 24));

    const root = svgNode('svg', {
      class: 'chart-svg',
      viewBox: '0 0 ' + width + ' ' + height,
      preserveAspectRatio: 'xMidYMid meet',
      role: 'img',
      'aria-label': byDay ? 'Активность по суткам' : 'Активность по часам'
    });

    // горизонтальная сетка и подписи оси Y
    for (let i = 0; i <= 4; i++) {
      const value = max * (1 - i / 4);
      const y = pad.top + plotH * (i / 4);
      root.appendChild(svgNode('line', { class: 'grid-line', x1: pad.left, x2: width - pad.right, y1: y, y2: y }));
      const label = svgNode('text', { class: 'axis-text', x: pad.left - 8, y: y + 4, 'text-anchor': 'end' });
      label.textContent = fmtAxis(value);
      root.appendChild(label);
    }

    root.appendChild(svgNode('line', {
      class: 'axis-line', x1: pad.left, x2: width - pad.right, y1: pad.top + plotH, y2: pad.top + plotH
    }));

    buckets.forEach(function (bucket, index) {
      const x = pad.left + step * index;
      const group = svgNode('g', { class: 'hour-col' });

      if (bucket.isCurrent) {
        group.appendChild(svgNode('rect', { class: 'hour-current', x: x, y: pad.top, width: step, height: plotH }));
      }
      group.appendChild(svgNode('rect', { class: 'hour-bg', x: x, y: pad.top, width: step, height: plotH }));

      let cursor = pad.top + plotH;
      const apps = (bucket.apps || []).filter(function (app) { return visible(app.appId); });
      const otherSeconds = (bucket.apps || []).filter(function (app) { return !visible(app.appId); })
        .reduce(function (sum, app) { return sum + (Number(app.seconds) || 0); }, 0);

      const segments = apps.map(function (app) {
        return { appId: app.appId, name: app.displayName || app.exeName, seconds: Number(app.seconds) || 0 };
      });
      if (otherSeconds > 0) segments.push({ appId: -1, name: 'Прочее', seconds: otherSeconds });

      segments.forEach(function (segment) {
        const segH = plotH * (segment.seconds / max);
        if (segH <= 0) return;
        cursor -= segH;
        group.appendChild(svgNode('rect', {
          class: 'seg',
          x: x + (step - barW) / 2,
          y: cursor,
          width: barW,
          height: segH,
          fill: colorFor(segment.appId)
        }));
      });

      const text = svgNode('text', {
        class: 'axis-text' + (bucket.isCurrent ? ' axis-text-current' : ''),
        x: x + step / 2,
        y: height - 10,
        'text-anchor': 'middle'
      });
      text.textContent = index % labelEvery === 0 ? (bucket.label || '') : '';
      group.appendChild(text);

      group.addEventListener('mousemove', function (event) {
        if (!chartDragging) showTooltip(event, bucket, tooltip);
      });
      group.addEventListener('mouseleave', function () { tooltip.hidden = true; });

      root.appendChild(group);
    });

    host.appendChild(root);
    attachDragSelection(root, buckets, pad, plotW, width, height);

    // легенда
    const legendItems = topIds.map(function (appId) {
      return { appId: appId, name: appLabel(buckets, appId), seconds: null };
    });
    legendItems.push({ appId: -1, name: 'Прочее', seconds: null });
    legendItems.forEach(function (item) {
      const hidden = state.hiddenApps.has(item.appId);
      const node = el('button', 'legend-item' + (hidden ? ' muted' : ''));
      node.type = 'button';
      node.title = hidden ? 'Показать' : 'Скрыть';
      const swatch = el('i', 'legend-swatch');
      swatch.style.background = item.appId === -1 ? 'var(--text-muted)' : colorFor(item.appId);
      node.appendChild(swatch);
      node.appendChild(el('span', null, item.name));
      node.addEventListener('click', function () {
        if (state.hiddenApps.has(item.appId)) state.hiddenApps.delete(item.appId);
        else state.hiddenApps.add(item.appId);
        renderChart(buckets, bucketKind);
      });
      legend.appendChild(node);
    });

    state.buckets = buckets;
    state.bucketKind = bucketKind;
  }

  /// Выделение мышью на графике: тянем по столбцам — получаем период.
  function attachDragSelection(svg, buckets, pad, plotW, width, height) {
    const overlay = svgNode('rect', {
      class: 'select-rect', x: pad.left, y: pad.top, width: 0, height: height - pad.top - pad.bottom,
      visibility: 'hidden'
    });
    svg.appendChild(overlay);

    let anchorX = null;
    const plotH = height - pad.top - pad.bottom;

    const toViewBoxX = function (event) {
      const box = svg.getBoundingClientRect();
      const scale = box.width > 0 ? width / box.width : 1;
      return (event.clientX - box.left) * scale;
    };
    const clampX = function (x) { return Math.max(pad.left, Math.min(pad.left + plotW, x)); };

    const start = function (event) {
      if (event.button !== 0) return;
      anchorX = clampX(toViewBoxX(event));
      chartDragging = false;
      overlay.setAttribute('x', anchorX);
      overlay.setAttribute('y', pad.top);
      overlay.setAttribute('width', 0);
      overlay.setAttribute('height', plotH);
      event.preventDefault();
    };

    const move = function (event) {
      if (anchorX === null) return;
      const x = clampX(toViewBoxX(event));
      if (Math.abs(x - anchorX) > 3) {
        chartDragging = true;
        overlay.setAttribute('visibility', 'visible');
      }
      overlay.setAttribute('x', Math.min(anchorX, x));
      overlay.setAttribute('width', Math.abs(x - anchorX));
    };

    const finish = function (event) {
      if (anchorX === null) return;
      const x = clampX(toViewBoxX(event));
      const from = Math.min(anchorX, x);
      const to = Math.max(anchorX, x);
      anchorX = null;
      overlay.setAttribute('visibility', 'hidden');

      const wasDragging = chartDragging;
      chartDragging = false;
      if (!wasDragging || to - from < 4 || !buckets.length) return;

      const step = plotW / buckets.length;
      const first = Math.max(0, Math.floor((from - pad.left) / step));
      const last = Math.min(buckets.length - 1, Math.floor((to - pad.left) / step));
      const fromBucket = buckets[first];
      const toBucket = buckets[last];
      if (!fromBucket || !toBucket) return;

      const kind = state.bucketKind === 'day' ? 'сут.' : 'ч';
      setRange(rangeInputValue(fromBucket.startUtc), rangeInputValue(toBucket.endUtc),
        'выделено ' + (last - first + 1) + ' ' + kind);
    };

    svg.addEventListener('mousedown', start);
    svg.addEventListener('mousemove', move);
    svg.addEventListener('mouseup', finish);
    svg.addEventListener('mouseleave', function (event) { if (anchorX !== null) finish(event); });
  }

  /// Значение для поля периода: локальное время с точностью до секунды.
  function rangeInputValue(iso) {
    const date = new Date(iso);
    if (isNaN(date.getTime())) return '';
    return date.getFullYear() + '-' + pad2(date.getMonth() + 1) + '-' + pad2(date.getDate()) + 'T' +
      pad2(date.getHours()) + ':' + pad2(date.getMinutes()) + ':' + pad2(date.getSeconds());
  }

  function showTooltip(event, bucket, tooltip) {
    clear(tooltip);
    const title = bucket.label
      ? bucket.label + ' — ' + fmtDuration(bucket.totalSeconds)
      : fmtDuration(bucket.totalSeconds);
    tooltip.appendChild(el('div', 'tooltip-title', title));
    const apps = (bucket.apps || []).slice().sort(function (a, b) {
      return (Number(b.seconds) || 0) - (Number(a.seconds) || 0);
    });
    if (!apps.length) {
      tooltip.appendChild(el('div', 'tooltip-row', 'нет активности'));
    }
    apps.forEach(function (app) {
      const row = el('div', 'tooltip-row');
      const name = el('span', 'name');
      const swatch = el('i', 'legend-swatch');
      swatch.style.background = colorFor(app.appId);
      name.appendChild(swatch);
      name.appendChild(el('span', null, app.displayName || app.exeName));
      row.appendChild(name);
      row.appendChild(el('span', 'tabular', fmtDuration(app.seconds)));
      tooltip.appendChild(row);
    });
    tooltip.hidden = false;

    const wrap = tooltip.parentElement;
    const rect = wrap.getBoundingClientRect();
    const left = Math.min(Math.max(0, event.clientX - rect.left + 14), Math.max(0, rect.width - 260));
    tooltip.style.left = left + 'px';
    tooltip.style.top = Math.max(0, event.clientY - rect.top + 14) + 'px';
  }

  function renderHoursTable(buckets) {
    const host = $('hours-table');
    clear(host);
    const table = el('table', 'table');
    const thead = el('thead');
    const headRow = el('tr');
    ['Период', 'Всего', 'Приложения (топ-3)'].forEach(function (title) {
      const th = el('th', null, title);
      th.scope = 'col';
      headRow.appendChild(th);
    });
    thead.appendChild(headRow);
    table.appendChild(thead);

    const tbody = el('tbody');
    buckets.forEach(function (bucket) {
      const row = el('tr');
      row.appendChild(el('td', 'tabular', (bucket.label || '') + (bucket.isCurrent ? ' •' : '')));
      row.appendChild(el('td', 'tabular', fmtDuration(bucket.totalSeconds)));
      const apps = (bucket.apps || []).slice().sort(function (a, b) {
        return (Number(b.seconds) || 0) - (Number(a.seconds) || 0);
      }).slice(0, 3).map(function (app) {
        return (app.displayName || app.exeName) + ' — ' + fmtDuration(app.seconds);
      }).join('; ');
      row.appendChild(el('td', null, apps || '—'));
      tbody.appendChild(row);
    });
    table.appendChild(tbody);
    host.appendChild(table);
  }

  // --------------------------------------------------------- ОБЗОР: лента дня

  function renderTimeline(timeline, topApps, range) {
    const host = $('timeline-host');
    clear(host);
    $('timeline-subtitle').textContent = timeline.length ? 'интервалов: ' + fmtNumber(timeline.length) : '';

    if (!timeline.length) {
      host.appendChild(el('div', 'chart-empty', 'За выбранный период интервалов активности нет.'));
      return;
    }

    const ids = topApps.slice(0, TOP_APPS).map(function (app) { return app.appId; });
    const names = new Map();
    topApps.forEach(function (app) { names.set(app.appId, app.displayName || app.exeName); });

    // Шкала ленты — выбранный период (а не обязательно сутки).
    const rangeStart = range && range.fromUtc ? new Date(range.fromUtc).getTime() : new Date(state.date + 'T00:00:00').getTime();
    const rangeEnd = range && range.toUtc ? new Date(range.toUtc).getTime() : rangeStart + DAY_MS;
    const span = Math.max(1, rangeEnd - rangeStart);

    const axis = el('div', 'gantt-axis');
    const tickCount = 8;
    for (let i = 0; i <= tickCount; i++) {
      const at = new Date(rangeStart + span * (i / tickCount));
      const shortSpan = span <= 2 * DAY_MS;
      const text = shortSpan
        ? pad2(at.getHours()) + ':' + pad2(at.getMinutes())
        : pad2(at.getDate()) + '.' + pad2(at.getMonth() + 1);
      const tick = el('span', null, text);
      tick.style.left = (i / tickCount * 100) + '%';
      axis.appendChild(tick);
    }
    host.appendChild(axis);

    const rows = el('div', 'gantt-rows');
    ids.forEach(function (appId) {
      const row = el('div', 'gantt-row');
      row.appendChild(el('div', 'gantt-label', names.get(appId) || ('#' + appId)));
      const track = el('div', 'gantt-track');
      timeline.filter(function (item) { return item.appId === appId; }).forEach(function (item) {
        const start = new Date(item.startUtc).getTime();
        const end = new Date(item.endUtc).getTime();
        if (isNaN(start) || isNaN(end)) return;
        const from = Math.max(0, Math.min(span, start - rangeStart));
        const to = Math.max(0, Math.min(span, end - rangeStart));
        if (to <= from) return;
        const bar = el('div', 'gantt-bar');
        bar.style.left = (from / span * 100) + '%';
        bar.style.width = Math.max(0.15, (to - from) / span * 100) + '%';
        bar.style.background = colorFor(appId);
        bar.title = fmtDateTime(item.startUtc) + ' – ' + fmtDateTime(item.endUtc) + ' · ' + fmtDuration(item.seconds)
          + (item.userName ? ' · ' + item.userName : '');
        track.appendChild(bar);
      });
      row.appendChild(track);
      rows.appendChild(row);
    });
    host.appendChild(rows);

    const legend = el('div', 'gantt-legend');
    const total = timeline.reduce(function (sum, item) { return sum + (Number(item.seconds) || 0); }, 0);
    legend.appendChild(el('span', null, 'Суммарно: ' + fmtDuration(total)));
    legend.appendChild(el('span', null, range && range.label
      ? 'Шкала: ' + range.label
      : 'Шкала: 24 часа локального времени сервера'));
    host.appendChild(legend);
  }

  // ------------------------------------------------------- ОБЗОР: пользователи

  function renderUsers(perUser) {
    const card = $('users-card');
    const host = $('users-host');
    clear(host);
    if (!perUser || perUser.length <= 1) {
      card.hidden = true;
      return;
    }
    card.hidden = false;
    $('users-subtitle').textContent = 'пользователей: ' + fmtNumber(perUser.length);
    perUser.forEach(function (user) {
      const item = el('div', 'bar-item');
      const label = el('div', 'bar-item-label');
      label.appendChild(el('span', null, user.userName || 'неизвестный'));
      label.appendChild(el('span', 'bar-num', fmtDuration(user.seconds) + ' · ' + fmtPercent(user.percent)));
      item.appendChild(label);
      const bar = el('div', 'bar');
      const fill = el('div', 'bar-fill');
      fill.style.width = Math.min(100, Number(user.percent) || 0) + '%';
      bar.appendChild(fill);
      item.appendChild(bar);
      host.appendChild(item);
    });
  }

  // ------------------------------------------------------------ ОБЗОР: события

  function renderEvents(events) {
    const host = $('events-host');
    clear(host);
    $('events-count').textContent = events.length ? 'событий: ' + fmtNumber(events.length) : '';
    if (!events.length) {
      host.appendChild(el('div', 'muted', 'Событий за сутки нет.'));
      return;
    }
    events.forEach(function (event) { host.appendChild(eventNode(event)); });
  }

  function eventNode(event) {
    const item = el('div', 'feed-item');
    item.appendChild(el('span', 'feed-time tabular', fmtClock(event.occurredUtc)));
    const badgeRow = el('div', 'badge-row');
    badgeRow.appendChild(el('span', severityClass(event.severity), severityText(event.severity)));
    item.appendChild(badgeRow);
    const body = el('div');
    body.appendChild(el('div', 'feed-rule', event.ruleName || 'правило'));
    body.appendChild(el('div', 'feed-message', event.message || ''));
    const sub = [];
    if (event.targetName) sub.push(event.targetName);
    if (event.limitSeconds) sub.push('лимит ' + fmtDuration(event.limitSeconds));
    if (event.actionType) sub.push('действие: ' + event.actionType + (event.actionStatus ? ' → ' + event.actionStatus : ''));
    if (sub.length) body.appendChild(el('div', 'feed-sub', sub.join(' · ')));
    item.appendChild(body);
    return item;
  }

  function renderAgentsInline(agents) {
    const alive = agents.filter(function (a) { return a.alive; }).length;
    const badge = $('health-badge');
    const text = $('health-text');
    if (!agents.length) {
      text.textContent = 'агенты ещё не зарегистрированы';
      badge.className = 'health health-bad';
      return;
    }
    text.textContent = 'агентов на связи: ' + alive + ' из ' + agents.length
      + (agents[0].lastReportUtc ? ' · последний отчёт ' + fmtClock(agents[0].lastReportUtc) : '');
    badge.className = 'health ' + (alive > 0 ? 'health-ok' : 'health-bad');
  }

  // ------------------------------------------------------------ ТОП приложений

  function renderApps() {
    const body = $('apps-table-body');
    clear(body);
    const needle = ($('apps-search').value || '').trim().toLowerCase();
    const apps = state.apps.filter(function (app) {
      if (!needle) return true;
      return (app.displayName || '').toLowerCase().indexOf(needle) >= 0
        || (app.exeName || '').toLowerCase().indexOf(needle) >= 0
        || (app.exePath || '').toLowerCase().indexOf(needle) >= 0;
    });

    $('apps-count').textContent = 'показано ' + fmtNumber(apps.length) + ' из ' + fmtNumber(state.apps.length)
      + (needle ? ' (поиск: ' + needle + ')' : '');

    if (!apps.length) {
      setMsg($('apps-msg'), state.apps.length ? 'Ничего не найдено по запросу.' : 'За выбранную дату приложений с активностью нет.', 'info');
    } else {
      setMsg($('apps-msg'), null);
    }

    apps.slice(0, 300).forEach(function (app) {
      const row = el('tr');
      if (app.isIgnored) row.className = 'app-ignored';

      const nameCell = el('td');
      const name = el('div', 'app-name');
      const dot = el('i', 'chip-swatch');
      dot.style.background = colorFor(app.id);
      dot.title = 'Цвет приложения на графике';
      name.appendChild(dot);
      name.appendChild(el('span', null, app.displayName || app.exeName));
      if (app.exePath) name.title = app.exePath;
      nameCell.appendChild(name);
      const sub = el('div', 'app-exe');
      sub.appendChild(el('span', null, app.exeName || ''));
      if (app.category) sub.appendChild(el('span', 'chip', app.category));
      if (app.isIgnored) sub.appendChild(el('span', 'badge badge-muted', 'вне учёта'));
      nameCell.appendChild(sub);
      row.appendChild(nameCell);

      row.appendChild(el('td', 'tabular', fmtDuration(app.todaySeconds)));

      const shareCell = el('td');
      shareCell.appendChild(shareBar(app.percentOfTracked));
      row.appendChild(shareCell);

      const groupCell = el('td');
      const chips = el('div', 'chips');
      (app.groupNames || []).forEach(function (groupName) {
        chips.appendChild(el('span', 'chip', groupName));
      });
      groupCell.appendChild(chips);
      row.appendChild(groupCell);

      const criticalCell = el('td');
      criticalCell.appendChild(criticalCellNode(app.critical));
      row.appendChild(criticalCell);

      const actions = el('td');
      actions.appendChild(actionsNode(app));
      row.appendChild(actions);

      body.appendChild(row);
    });
  }

  function shareBar(percent) {
    const wrap = el('div', 'bar bar-lg');
    const fill = el('div', 'bar-fill');
    fill.style.width = Math.min(100, Math.max(0, Number(percent) || 0)) + '%';
    wrap.appendChild(fill);
    const text = el('div', 'bar-num', fmtPercent(percent));
    const box = el('div', 'bar-item');
    box.appendChild(wrap);
    box.appendChild(text);
    return box;
  }

  function criticalCellNode(critical) {
    if (!critical) return el('span', 'muted', 'нет правила');
    const box = el('div', 'bar-item');
    const percent = Math.max(0, Number(critical.percent) || 0);
    const bar = el('div', 'bar');
    const fill = el('div', 'bar-fill');
    fill.style.width = Math.min(100, percent) + '%';
    fill.style.background = critical.exceeded ? 'var(--err)' : (percent >= 80 ? 'var(--warn)' : 'var(--accent)');
    bar.appendChild(fill);
    box.appendChild(bar);
    const label = el('div', 'bar-item-label');
    label.appendChild(el('span', 'muted', critical.ruleName || ''));
    label.appendChild(el('span', 'bar-num' + (critical.exceeded ? ' text-err' : ''),
      fmtDuration(critical.usedSeconds) + ' / ' + fmtDuration(critical.limitSeconds)));
    box.appendChild(label);
    return box;
  }

  function actionsNode(app) {
    const wrap = el('div', 'edit-actions');
    const rename = el('button', 'btn btn-small', 'имя');
    rename.type = 'button';
    rename.title = 'Задать отображаемое имя';
    rename.addEventListener('click', async function () {
      const value = window.prompt('Отображаемое имя для ' + app.exeName, app.displayName || '');
      if (value === null) return;
      await saveApp(app.id, { friendlyName: value.trim() });
    });

    const category = el('button', 'btn btn-small', 'категория');
    category.type = 'button';
    category.title = 'Задать категорию';
    category.addEventListener('click', async function () {
      const value = window.prompt('Категория для ' + app.exeName, app.category || '');
      if (value === null) return;
      await saveApp(app.id, { category: value.trim() });
    });

    const ignore = el('button', 'btn btn-small', app.isIgnored ? 'учитывать' : 'игнорировать');
    ignore.type = 'button';
    ignore.title = 'Исключить или вернуть приложение в статистику';
    ignore.addEventListener('click', async function () {
      await saveApp(app.id, { isIgnored: !app.isIgnored });
    });

    const intervals = el('button', 'btn btn-small', 'детали');
    intervals.type = 'button';
    intervals.title = 'Показать интервалы активности';
    intervals.addEventListener('click', function () { showIntervals(app); });

    const processes = el('button', 'btn btn-small btn-danger', 'процессы');
    processes.type = 'button';
    processes.title = 'Показать процессы приложения в системе и завершить их';
    processes.addEventListener('click', function () { openProcessesDialog(app); });

    wrap.appendChild(rename);
    wrap.appendChild(category);
    wrap.appendChild(ignore);
    wrap.appendChild(intervals);
    wrap.appendChild(processes);
    return wrap;
  }

  // ------------------------------------------------------- процессы и завершение

  /// Мини-диалог: список процессов приложения с возможностью завершить их.
  async function openProcessesDialog(app) {
    const dialog = ensureModal();
    const body = dialog.querySelector('.modal-body');
    const title = dialog.querySelector('.modal-title');
    title.textContent = 'Процессы: ' + (app.displayName || app.exeName);
    clear(body);
    body.appendChild(el('div', 'muted', 'Загрузка…'));
    dialog.hidden = false;

    let data = null;
    try {
      data = await api('api/processes?appId=' + app.id);
    } catch (error) {
      clear(body);
      body.appendChild(el('div', 'msg msg-error', 'Не удалось получить список процессов: ' + describeError(error)));
      return;
    }

    clear(body);
    if (!data.processes || !data.processes.length) {
      body.appendChild(el('div', 'muted', 'Процессы ' + (app.exeName || '') + ' в системе не найдены (приложение не запущено).'));
      return;
    }

    const list = el('div', 'process-list');
    data.processes.forEach(function (proc) {
      const row = el('div', 'process-row');
      const info = el('div', 'process-info');
      info.appendChild(el('div', 'process-name', proc.exeName + ' · PID ' + proc.pid));
      const meta = [];
      meta.push('сеанс ' + proc.sessionId);
      if (proc.memoryMb) meta.push(proc.memoryMb.toFixed(0) + ' МБ');
      if (proc.startedUtc) meta.push('с ' + fmtDateTime(proc.startedUtc));
      if (proc.windowTitle) meta.push('«' + proc.windowTitle + '»');
      info.appendChild(el('div', 'process-meta muted', meta.join(' · ')));
      row.appendChild(info);

      if (proc.isProtected) {
        const badge = el('span', 'badge badge-muted', 'защищён');
        badge.title = proc.protectedReason || '';
        row.appendChild(badge);
      } else {
        const kill = el('button', 'btn btn-small btn-danger', 'завершить');
        kill.type = 'button';
        kill.addEventListener('click', function () {
          if (!window.confirm('Завершить процесс ' + proc.exeName + ' (PID ' + proc.pid + ', сеанс ' + proc.sessionId + ')?\n' +
              'Несохранённые данные приложения будут потеряны.')) return;
          killProcesses({ appId: app.id, pid: proc.pid, reason: 'завершение из Web-панели' }, function () {
            openProcessesDialog(app);
          });
        });
        row.appendChild(kill);
      }
      list.appendChild(row);
    });
    body.appendChild(list);

    const actions = el('div', 'modal-actions');
    const killAll = el('button', 'btn btn-danger', 'Завершить все процессы приложения');
    killAll.type = 'button';
    killAll.disabled = !data.canKill;
    killAll.addEventListener('click', function () {
      if (!window.confirm('Завершить все процессы ' + app.exeName + ' во всех сеансах (' + data.processes.length + ' шт.)?\n' +
          'Несохранённые данные приложения будут потеряны.')) return;
      killProcesses({ appId: app.id, reason: 'завершение всех процессов из Web-панели' }, function () {
        openProcessesDialog(app);
      });
    });
    actions.appendChild(killAll);

    const refresh = el('button', 'btn', 'Обновить список');
    refresh.type = 'button';
    refresh.addEventListener('click', function () { openProcessesDialog(app); });
    actions.appendChild(refresh);

    const close = el('button', 'btn', 'Закрыть');
    close.type = 'button';
    close.addEventListener('click', function () { dialog.hidden = true; });
    actions.appendChild(close);
    body.appendChild(actions);
  }

  async function killProcesses(payload, onDone) {
    try {
      const result = await api('api/processes/kill', { method: 'POST', body: payload });
      const failed = (result.results || []).filter(function (r) { return r.status === 'failed'; });
      const protectedOnes = (result.results || []).filter(function (r) { return r.status === 'protected'; });
      let message = 'Завершено процессов: ' + fmtNumber(result.killedCount);
      if (protectedOnes.length) message += ', защищённых пропущено: ' + protectedOnes.length;
      if (failed.length) message += ', ошибок: ' + failed.length + ' (' + failed[0].message + ')';
      toast(message, result.killedCount > 0 ? 'ok' : 'warn');

      // Обновляем события и статистику: действие попадает в журнал событий.
      loadAll();
      if (onDone) onDone();
    } catch (error) {
      toast('Не удалось завершить процесс: ' + describeError(error), 'error');
    }
  }

  /// Простое модальное окно (создаётся один раз).
  function ensureModal() {
    let dialog = $('appactive-modal');
    if (dialog) return dialog;

    dialog = el('div', 'modal');
    dialog.id = 'appactive-modal';
    dialog.hidden = true;

    const backdrop = el('div', 'modal-backdrop');
    backdrop.addEventListener('click', function () { dialog.hidden = true; });
    dialog.appendChild(backdrop);

    const windowNode = el('div', 'modal-window');
    const head = el('div', 'modal-head');
    head.appendChild(el('div', 'modal-title', 'Процессы'));
    const closeButton = el('button', 'modal-close', '×');
    closeButton.type = 'button';
    closeButton.title = 'Закрыть';
    closeButton.addEventListener('click', function () { dialog.hidden = true; });
    head.appendChild(closeButton);
    windowNode.appendChild(head);
    windowNode.appendChild(el('div', 'modal-body'));
    dialog.appendChild(windowNode);

    document.body.appendChild(dialog);
    return dialog;
  }

  async function saveApp(id, patch) {
    try {
      await api('api/apps/' + id, { method: 'PUT', body: patch });
      toast('Приложение обновлено', 'ok');
      await refreshCore();
    } catch (error) {
      toast('Не удалось обновить приложение: ' + describeError(error), error.status === 401 || error.status === 403 ? 'warn' : 'error');
    }
  }

  async function showIntervals(app) {
    try {
      const data = await api(query('api/apps/' + app.id + '/intervals'));
      const intervals = (data && data.intervals) || [];
      const lines = intervals.slice(0, 40).map(function (item) {
        return fmtClock(item.startUtc) + '–' + fmtClock(item.endUtc) + ' · ' + fmtDuration(item.seconds)
          + ' · ' + (item.userName || '');
      });
      if (!lines.length) {
        toast('За выбранный период интервалов нет', 'info');
        return;
      }
      toast((app.displayName || app.exeName) + ': ' + lines.length + ' интервалов'
        + (data.label ? ' (' + data.label + ')' : '') + ', всего '
        + fmtDuration(data.totalSeconds) + '. Первые: ' + lines.slice(0, 3).join('; '), 'info');
    } catch (error) {
      toast('Не удалось получить интервалы: ' + describeError(error), 'error');
    }
  }

  // ----------------------------------------------------------- КРИТИЧЕСКОЕ

  function renderCriticalStatus() {
    const host = $('critical-list');
    clear(host);
    if (!state.criticalStatus.length) {
      host.appendChild(el('div', 'muted', 'Правила критического времени не заданы. Добавьте правило ниже.'));
      return;
    }
    state.criticalStatus.forEach(function (rule) {
      const item = el('div', 'critical-item');
      const head = el('div', 'critical-item-head');
      head.appendChild(el('div', 'critical-name', rule.ruleName));
      const row = el('div', 'badge-row');
      row.appendChild(el('span', severityClass(rule.severity), severityText(rule.severity)));
      if (!rule.enabled) row.appendChild(el('span', 'badge badge-muted', 'выключено'));
      if (!rule.applicableToday) row.appendChild(el('span', 'badge badge-muted', 'не сегодня'));
      if (rule.exceeded) row.appendChild(el('span', 'badge badge-critical', 'превышено'));
      head.appendChild(row);
      item.appendChild(head);

      item.appendChild(el('div', 'critical-target', rule.targetName || ''));

      const bar = el('div', 'bar bar-lg');
      const fill = el('div', 'bar-fill');
      const percent = Math.max(0, Number(rule.percent) || 0);
      fill.style.width = Math.min(100, percent) + '%';
      fill.style.background = rule.exceeded ? 'var(--err)' : (percent >= 80 ? 'var(--warn)' : 'var(--accent)');
      bar.appendChild(fill);
      item.appendChild(bar);

      const meta = el('div', 'critical-meta');
      meta.appendChild(el('span', null, fmtDuration(rule.usedSeconds) + ' из ' + fmtDuration(rule.limitSeconds)));
      meta.appendChild(el('span', rule.exceeded ? 'text-err' : 'muted', fmtPercent(percent)));
      item.appendChild(meta);

      const remaining = Number(rule.limitSeconds) - Number(rule.usedSeconds);
      item.appendChild(el('div', 'critical-remaining' + (rule.exceeded ? ' text-err' : ' muted'),
        rule.exceeded
          ? 'превышено на ' + fmtDuration(Math.abs(remaining))
          : 'осталось ' + fmtDuration(remaining)));

      if (rule.firstExceededUtc) {
        item.appendChild(el('div', 'critical-remaining muted', 'первое превышение: ' + fmtDateTime(rule.firstExceededUtc)));
      }
      host.appendChild(item);
    });
  }

  function renderCriticalRules() {
    const body = $('cr-table-body');
    clear(body);
    $('cr-count').textContent = 'правил: ' + fmtNumber(state.criticalRules.length);

    state.criticalRules.forEach(function (rule) {
      const row = el('tr');
      row.appendChild(el('td', null, rule.name));
      row.appendChild(el('td', null, rule.targetName + ' (' + (rule.targetType === 'group' ? 'группа' : 'приложение') + ')'));
      row.appendChild(el('td', 'tabular', fmtDuration(rule.dailyLimitSeconds) + ' (' + fmtMinutes(rule.dailyLimitSeconds) + ' мин)'));
      row.appendChild(el('td', null, daysText(rule.daysOfWeek)));
      row.appendChild(el('td', null, severityText(rule.severity)));
      row.appendChild(el('td', null, rule.enabled ? 'да' : 'нет'));

      const actions = el('td');
      const wrap = el('div', 'edit-actions');
      const edit = el('button', 'btn btn-small', 'изменить');
      edit.type = 'button';
      edit.addEventListener('click', function () { fillCriticalForm(rule); });
      const remove = el('button', 'btn btn-small btn-danger', 'удалить');
      remove.type = 'button';
      remove.addEventListener('click', async function () {
        if (!window.confirm('Удалить правило «' + rule.name + '»?')) return;
        try {
          await api('api/critical-rules/' + rule.id, { method: 'DELETE' });
          if (state.edit.critical === rule.id) resetCriticalForm();
          toast('Правило удалено', 'ok');
          await loadAll();
        } catch (error) {
          toast('Не удалось удалить правило: ' + describeError(error), 'error');
        }
      });
      wrap.appendChild(edit);
      wrap.appendChild(remove);
      actions.appendChild(wrap);
      row.appendChild(actions);
      body.appendChild(row);
    });
  }

  function renderCriticalTargets() {
    const type = $('cr-target-type').value;
    fillTargetSelect($('cr-target'), type);
  }

  function fillTargetSelect(select, type) {
    const current = select.value;
    clear(select);
    if (type === 'group') {
      state.groups.forEach(function (group) {
        const option = el('option', null, group.name + ' (' + group.appCount + ' прил.)');
        option.value = String(group.id);
        select.appendChild(option);
      });
    } else {
      state.apps.filter(function (app) { return !app.isIgnored; }).forEach(function (app) {
        const option = el('option', null, app.displayName + ' (' + app.exeName + ')');
        option.value = String(app.id);
        select.appendChild(option);
      });
    }
    if (current && select.querySelector('option[value="' + current + '"]')) select.value = current;
  }

  function fillCriticalForm(rule) {
    state.edit.critical = rule.id;
    $('cr-form-title').textContent = 'Правило #' + rule.id;
    $('cr-name').value = rule.name || '';
    $('cr-target-type').value = rule.targetType || 'app';
    fillTargetSelect($('cr-target'), rule.targetType || 'app');
    $('cr-target').value = String(rule.targetId);
    $('cr-limit').value = Math.max(1, fmtMinutes(rule.dailyLimitSeconds));
    $('cr-limit-hint').textContent = 'Сохраняется в секундах: ' + Math.round(rule.dailyLimitSeconds);
    $('cr-severity').value = rule.severity || 'warning';
    $('cr-enabled').checked = !!rule.enabled;
    const days = rule.daysOfWeek || [];
    Array.prototype.forEach.call($('cr-days').querySelectorAll('input[type="checkbox"]'), function (box) {
      box.checked = days.indexOf(Number(box.value)) >= 0;
    });
    $('cr-delete').hidden = false;
    setMsg($('cr-form-msg'), null);
    selectTab('critical');
  }

  function resetCriticalForm() {
    state.edit.critical = null;
    $('cr-form-title').textContent = 'Новое правило';
    $('cr-name').value = '';
    $('cr-target-type').value = 'app';
    fillTargetSelect($('cr-target'), 'app');
    $('cr-limit').value = 120;
    $('cr-limit-hint').textContent = 'Сохраняется в секундах.';
    $('cr-severity').value = 'warning';
    $('cr-enabled').checked = true;
    Array.prototype.forEach.call($('cr-days').querySelectorAll('input[type="checkbox"]'), function (box) { box.checked = false; });
    $('cr-delete').hidden = true;
    setMsg($('cr-form-msg'), null);
  }

  async function submitCritical(event) {
    event.preventDefault();
    const name = ($('cr-name').value || '').trim();
    const limitMinutes = Number($('cr-limit').value) || 0;
    if (!name) { setMsg($('cr-form-msg'), 'Укажите название правила', 'error'); return; }
    if (limitMinutes <= 0) { setMsg($('cr-form-msg'), 'Укажите лимит в минутах', 'error'); return; }

    const days = [];
    Array.prototype.forEach.call($('cr-days').querySelectorAll('input[type="checkbox"]'), function (box) {
      if (box.checked) days.push(Number(box.value));
    });

    const body = {
      name: name,
      targetType: $('cr-target-type').value,
      targetId: Number($('cr-target').value),
      dailyLimitSeconds: Math.round(limitMinutes * 60),
      severity: $('cr-severity').value,
      enabled: $('cr-enabled').checked,
      daysOfWeek: days
    };

    try {
      if (state.edit.critical) {
        await api('api/critical-rules/' + state.edit.critical, { method: 'PUT', body: body });
        toast('Правило обновлено', 'ok');
      } else {
        await api('api/critical-rules', { method: 'POST', body: body });
        toast('Правило создано', 'ok');
      }
      resetCriticalForm();
      await loadAll();
    } catch (error) {
      setMsg($('cr-form-msg'), 'Ошибка: ' + describeError(error), 'error');
    }
  }

  // ------------------------------------------------------------------ ГРУППЫ

  function renderGroups() {
    const host = $('groups-list');
    clear(host);
    $('groups-count').textContent = 'групп: ' + fmtNumber(state.groups.length);

    if (!state.groups.length) {
      host.appendChild(el('div', 'muted', 'Группы не созданы. Группа объединяет приложения для общего критического лимита.'));
      return;
    }

    state.groups.forEach(function (group) {
      const card = el('div', 'group-card');
      const head = el('div', 'group-head');
      const dot = el('span', 'group-dot');
      dot.style.background = group.color || 'var(--accent)';
      head.appendChild(dot);
      head.appendChild(el('div', 'group-name', group.name));
      head.appendChild(el('span', 'muted', 'приложений: ' + fmtNumber(group.appCount) + ' · ' + fmtDuration(group.todaySeconds)));
      card.appendChild(head);

      if (group.description) card.appendChild(el('div', 'group-desc', group.description));
      if (group.critical) {
        const info = el('div', 'critical-meta');
        info.appendChild(el('span', null, 'Лимит: ' + fmtDuration(group.critical.limitSeconds) + ' · использовано ' + fmtDuration(group.critical.usedSeconds)));
        info.appendChild(el('span', group.critical.exceeded ? 'text-err' : 'muted', fmtPercent(group.critical.percent)));
        card.appendChild(info);
      }

      const members = el('div', 'group-members');
      (group.apps || []).forEach(function (app) {
        const chip = el('span', 'chip');
        const swatch = el('i', 'chip-swatch');
        swatch.style.background = colorFor(app.id);
        chip.appendChild(swatch);
        chip.appendChild(el('span', null, app.displayName + ' — ' + fmtDuration(app.todaySeconds)));
        members.appendChild(chip);
      });
      if (!(group.apps || []).length) members.appendChild(el('span', 'muted', 'состав пуст'));
      card.appendChild(members);

      const actions = el('div', 'group-actions');
      const edit = el('button', 'btn btn-small', 'изменить состав');
      edit.type = 'button';
      edit.addEventListener('click', function () { fillGroupForm(group); });
      actions.appendChild(edit);
      card.appendChild(actions);
      host.appendChild(card);
    });
  }

  function renderGroupPicker() {
    const select = $('g-apps');
    const checked = new Set();
    Array.prototype.forEach.call(select.querySelectorAll('input[type="checkbox"]'), function (box) {
      if (box.checked) checked.add(Number(box.value));
    });
    renderAppPicker(select, checked);
  }

  function renderAppPicker() {
    if (!state.edit.group) renderAppPickerTo($('g-apps'), new Set(state.pendingGroupApps || []));
  }

  function renderAppPickerTo(host, checkedSet) {
    clear(host);
    const apps = state.apps.filter(function (app) { return !app.isIgnored; });
    $('g-apps-hint').textContent = apps.length
      ? 'Отмечено: ' + checkedSet.size + ' из ' + apps.length
      : 'Список приложений пуст — сначала соберите статистику.';
    if (!apps.length) {
      host.appendChild(el('div', 'picker-empty', 'Нет приложений для выбора.'));
      return;
    }
    apps.forEach(function (app) {
      const item = el('label', 'picker-item');
      const box = el('input');
      box.type = 'checkbox';
      box.value = String(app.id);
      box.checked = checkedSet.has(app.id);
      box.addEventListener('change', function () {
        if (box.checked) checkedSet.add(app.id); else checkedSet.delete(app.id);
        state.pendingGroupApps = Array.from(checkedSet);
        $('g-apps-hint').textContent = 'Отмечено: ' + checkedSet.size + ' из ' + apps.length;
      });
      item.appendChild(box);
      const swatch = el('i', 'chip-swatch');
      swatch.style.background = colorFor(app.id);
      item.appendChild(swatch);
      item.appendChild(el('span', null, app.displayName + ' (' + app.exeName + ') — ' + fmtDuration(app.todaySeconds)));
      host.appendChild(item);
    });
  }

  function fillGroupForm(group) {
    state.edit.group = group.id;
    state.pendingGroupApps = (group.appIds || []).slice();
    $('g-form-title').textContent = 'Группа #' + group.id;
    $('g-name').value = group.name || '';
    $('g-desc').value = group.description || '';
    $('g-color').value = /^#[0-9a-f]{6}$/i.test(group.color || '') ? group.color : '#4f8cff';
    renderAppPickerTo($('g-apps'), new Set(state.pendingGroupApps));
    $('g-delete').hidden = false;
    setMsg($('g-form-msg'), null);
  }

  function resetGroupForm() {
    state.edit.group = null;
    state.pendingGroupApps = [];
    $('g-form-title').textContent = 'Новая группа';
    $('g-name').value = '';
    $('g-desc').value = '';
    $('g-color').value = '#4f8cff';
    renderAppPickerTo($('g-apps'), new Set());
    $('g-delete').hidden = true;
    setMsg($('g-form-msg'), null);
  }

  async function submitGroup(event) {
    event.preventDefault();
    const name = ($('g-name').value || '').trim();
    if (!name) { setMsg($('g-form-msg'), 'Укажите название группы', 'error'); return; }
    const body = {
      name: name,
      description: ($('g-desc').value || '').trim(),
      color: $('g-color').value,
      appIds: state.pendingGroupApps || []
    };
    try {
      if (state.edit.group) {
        await api('api/groups/' + state.edit.group, { method: 'PUT', body: body });
        toast('Группа обновлена', 'ok');
      } else {
        await api('api/groups', { method: 'POST', body: body });
        toast('Группа создана', 'ok');
      }
      resetGroupForm();
      await loadAll();
    } catch (error) {
      setMsg($('g-form-msg'), 'Ошибка: ' + describeError(error), 'error');
    }
  }

  // ---------------------------------------------------------------- WORKFLOW

  function renderWorkflowRules() {
    const body = $('wf-table-body');
    clear(body);
    $('wf-count').textContent = 'правил: ' + fmtNumber(state.workflowRules.length);

    state.workflowRules.forEach(function (rule) {
      const row = el('tr');
      row.appendChild(el('td', null, rule.name));
      row.appendChild(el('td', null, rule.triggerType === 'schedule'
        ? 'каждые ' + rule.scheduleMinutes + ' мин'
        : 'превышение правила #' + (rule.criticalRuleId || '—')));
      row.appendChild(el('td', null, rule.actionType + (rule.actionTarget ? ' → ' + rule.actionTarget : '')));
      row.appendChild(el('td', 'tabular', rule.cooldownMinutes + ' мин'));
      row.appendChild(el('td', null, rule.enabled ? 'да' : 'нет'));
      row.appendChild(el('td', 'tabular', rule.lastRunUtc ? fmtDateTime(rule.lastRunUtc) + (rule.lastStatus ? ' (' + rule.lastStatus + ')' : '') : '—'));

      const actions = el('td');
      const wrap = el('div', 'edit-actions');
      const edit = el('button', 'btn btn-small', 'изменить');
      edit.type = 'button';
      edit.addEventListener('click', function () { fillWorkflowForm(rule); });
      const remove = el('button', 'btn btn-small btn-danger', 'удалить');
      remove.type = 'button';
      remove.addEventListener('click', async function () {
        if (!window.confirm('Удалить правило «' + rule.name + '»?')) return;
        try {
          await api('api/workflow/rules/' + rule.id, { method: 'DELETE' });
          if (state.edit.workflow === rule.id) resetWorkflowForm();
          toast('Правило удалено', 'ok');
          await loadAll();
        } catch (error) {
          toast('Не удалось удалить правило: ' + describeError(error), 'error');
        }
      });
      wrap.appendChild(edit);
      wrap.appendChild(remove);
      actions.appendChild(wrap);
      row.appendChild(actions);
      body.appendChild(row);
    });
  }

  function renderWorkflowTargets() {
    const type = $('wf-target-type').value;
    const select = $('wf-target');
    const current = select.value;
    clear(select);
    if (type === 'group') {
      state.groups.forEach(function (group) {
        const option = el('option', null, group.name);
        option.value = String(group.id);
        select.appendChild(option);
      });
    } else {
      state.apps.forEach(function (app) {
        const option = el('option', null, app.displayName + ' (' + app.exeName + ')');
        option.value = String(app.id);
        select.appendChild(option);
      });
    }
    if (current && select.querySelector('option[value="' + current + '"]')) select.value = current;
  }

  function renderWorkflowCriticalRules() {
    const select = $('wf-critical-rule');
    const current = select.value;
    clear(select);
    state.criticalRules.forEach(function (rule) {
      const option = el('option', null, rule.name + ' — ' + rule.targetName);
      option.value = String(rule.id);
      select.appendChild(option);
    });
    if (!state.criticalRules.length) {
      const option = el('option', null, '— нет правил критического времени —');
      option.value = '';
      select.appendChild(option);
    } else if (current && select.querySelector('option[value="' + current + '"]')) {
      select.value = current;
    }
  }

  function syncWorkflowForm() {
    const schedule = $('wf-trigger').value === 'schedule';
    $('wf-critical-wrap').hidden = schedule;
    $('wf-schedule-wrap').hidden = !schedule;
    $('wf-target-type-wrap').hidden = !schedule;
    $('wf-target-wrap').hidden = !schedule;
    $('wf-threshold-wrap').hidden = !schedule;
    const actionType = $('wf-action-type').value;
    $('wf-action-target-wrap').hidden = actionType === 'event';
    if (actionType === 'webhook') $('wf-action-target').placeholder = 'https://host/hook';
    if (actionType === 'script') $('wf-action-target').placeholder = 'C:\\scripts\\alert.cmd';
  }

  function fillWorkflowForm(rule) {
    state.edit.workflow = rule.id;
    $('wf-form-title').textContent = 'Правило #' + rule.id;
    $('wf-name').value = rule.name || '';
    $('wf-trigger').value = rule.triggerType || 'criticalExceeded';
    renderWorkflowCriticalRules();
    if (rule.criticalRuleId) $('wf-critical-rule').value = String(rule.criticalRuleId);
    $('wf-schedule').value = rule.scheduleMinutes || 5;
    $('wf-target-type').value = rule.targetType || 'app';
    renderWorkflowTargets();
    if (rule.targetId) $('wf-target').value = String(rule.targetId);
    $('wf-threshold').value = rule.thresholdSeconds ? Math.max(1, fmtMinutes(rule.thresholdSeconds)) : 60;
    $('wf-action-type').value = rule.actionType || 'event';
    $('wf-action-target').value = rule.actionTarget || '';
    $('wf-cooldown').value = rule.cooldownMinutes || 0;
    $('wf-enabled').checked = !!rule.enabled;
    $('wf-delete').hidden = false;
    syncWorkflowForm();
    setMsg($('wf-form-msg'), null);
  }

  function resetWorkflowForm() {
    state.edit.workflow = null;
    $('wf-form-title').textContent = 'Новое правило реакции';
    $('wf-name').value = '';
    $('wf-trigger').value = 'criticalExceeded';
    renderWorkflowCriticalRules();
    $('wf-schedule').value = 5;
    $('wf-target-type').value = 'app';
    renderWorkflowTargets();
    $('wf-threshold').value = 60;
    $('wf-action-type').value = 'event';
    $('wf-action-target').value = '';
    $('wf-cooldown').value = 60;
    $('wf-enabled').checked = true;
    $('wf-delete').hidden = true;
    syncWorkflowForm();
    setMsg($('wf-form-msg'), null);
  }

  async function submitWorkflow(event) {
    event.preventDefault();
    const name = ($('wf-name').value || '').trim();
    if (!name) { setMsg($('wf-form-msg'), 'Укажите название правила', 'error'); return; }
    const schedule = $('wf-trigger').value === 'schedule';

    const body = {
      name: name,
      enabled: $('wf-enabled').checked,
      triggerType: $('wf-trigger').value,
      actionType: $('wf-action-type').value,
      actionTarget: ($('wf-action-target').value || '').trim(),
      cooldownMinutes: Number($('wf-cooldown').value) || 0
    };
    if (schedule) {
      body.scheduleMinutes = Number($('wf-schedule').value) || 5;
      body.targetType = $('wf-target-type').value;
      body.targetId = Number($('wf-target').value);
      body.thresholdSeconds = Math.round((Number($('wf-threshold').value) || 0) * 60);
    } else {
      body.criticalRuleId = Number($('wf-critical-rule').value);
    }

    try {
      if (state.edit.workflow) {
        await api('api/workflow/rules/' + state.edit.workflow, { method: 'PUT', body: body });
        toast('Правило обновлено', 'ok');
      } else {
        await api('api/workflow/rules', { method: 'POST', body: body });
        toast('Правило создано', 'ok');
      }
      resetWorkflowForm();
      await loadAll();
    } catch (error) {
      setMsg($('wf-form-msg'), 'Ошибка: ' + describeError(error), 'error');
    }
  }

  function renderWorkflowEvents(events) {
    const host = $('wf-events');
    clear(host);
    $('wf-events-count').textContent = events.length ? 'событий: ' + fmtNumber(events.length) : '';
    if (!events.length) {
      host.appendChild(el('div', 'muted', 'Событий workflow за сутки нет.'));
      return;
    }
    events.forEach(function (event) { host.appendChild(eventNode(event)); });
  }

  async function runWorkflowNow() {
    setMsg($('wf-run-result'), 'Выполняется…', 'info');
    try {
      const result = await api('api/workflow/run', { method: 'POST', body: { date: state.date } });
      const actions = (result && result.actions) || [];
      const summary = 'Событий создано: ' + fmtNumber(result.eventsCreated) + '. Действий: ' + actions.length
        + (actions.length ? ' — ' + actions.map(function (a) { return a.ruleName + ': ' + a.status; }).join('; ') : '');
      setMsg($('wf-run-result'), summary, 'ok');
      toast('Движок правил выполнен', 'ok');
      await loadAll();
    } catch (error) {
      setMsg($('wf-run-result'), 'Ошибка: ' + describeError(error), 'error');
    }
  }

  // --------------------------------------------------------------- НАСТРОЙКИ

  function renderSettings(settings) {
    $('s-poll').value = settings.pollIntervalSeconds;
    $('s-idle').value = settings.activityIdleSeconds;
    $('s-flush').value = settings.flushSeconds;
    $('s-retention').value = settings.retentionDays;
    $('s-heartbeat').value = settings.agentHeartbeatSeconds;
    $('s-monitor').checked = !!settings.monitorEnabled;
    $('s-titles').checked = !!settings.collectWindowTitles;
    $('s-ignore').value = (settings.ignoreProcesses || []).join('\n');
    $('settings-updated').textContent = settings.updatedUtc ? 'изменено ' + fmtDateTime(settings.updatedUtc) : '';
    updateLoadHint();
  }

  function updateLoadHint() {
    const poll = Number($('s-poll').value) || 5;
    const node = $('settings-load');
    if (!node) return;
    const loadText = poll <= 2 ? 'высокая' : poll <= 10 ? 'низкая' : poll <= 60 ? 'очень низкая' : 'минимальная';
    node.textContent = 'Ориентировочная нагрузка на ЦП при интервале ' + poll + ' с: ' + loadText
      + '. Один такт — это чтение активного окна и времени простоя, доли миллисекунды.';
  }

  async function submitSettings(event) {
    event.preventDefault();
    const body = {
      pollIntervalSeconds: Number($('s-poll').value) || 5,
      activityIdleSeconds: Number($('s-idle').value) || 60,
      flushSeconds: Number($('s-flush').value) || 15,
      retentionDays: Number($('s-retention').value) || 90,
      agentHeartbeatSeconds: Number($('s-heartbeat').value) || 60,
      monitorEnabled: $('s-monitor').checked,
      collectWindowTitles: $('s-titles').checked,
      ignoreProcesses: ($('s-ignore').value || '').split('\n').map(function (line) { return line.trim(); }).filter(Boolean)
    };
    try {
      const updated = await api('api/settings', { method: 'PUT', body: body });
      state.settings = updated;
      renderSettings(updated);
      setMsg($('settings-msg'), 'Настройки сохранены. Агенты применят интервал в течение одного цикла опроса.', 'ok');
      toast('Настройки сохранены', 'ok');
    } catch (error) {
      setMsg($('settings-msg'), 'Ошибка: ' + describeError(error), 'error');
    }
  }

  // ------------------------------------------------------------ ДИАГНОСТИКА

  function kvBlock(title, rows) {
    const block = el('div', 'kv-block');
    block.appendChild(el('div', 'kv-title', title));
    const list = el('dl', 'kv-list');
    rows.forEach(function (pair) {
      const row = el('div', 'kv-row');
      row.appendChild(el('dt', null, pair[0]));
      row.appendChild(el('dd', null, pair[1]));
      list.appendChild(row);
    });
    block.appendChild(list);
    return block;
  }

  function renderHealth(health) {
    const badge = $('health-badge');
    const text = $('health-text');
    badge.className = 'health health-ok';
    text.textContent = 'служба работает с ' + fmtClock(health.startedUtc);
    setOffline(false);
  }

  function renderDiagnostics(diag) {
    const host = $('diag-summary');
    clear(host);
    if (!diag) {
      setMsg($('diag-msg'), 'Данные диагностики недоступны.', 'warn');
      return;
    }
    setMsg($('diag-msg'), null);

    const service = diag.service || {};
    const ingest = diag.ingest || {};
    const monitoring = diag.monitoring || {};
    const database = diag.database || {};
    const workflow = diag.workflow || {};

    host.appendChild(kvBlock('Служба', [
      ['Загрузка ЦП', fmtPercent(service.cpuPercent)],
      ['Память', (Number(service.workingSetMb) || 0).toFixed(1) + ' МБ'],
      ['Потоки / дескрипторы', fmtNumber(service.threadCount) + ' / ' + fmtNumber(service.handleCount)],
      ['Время работы', fmtDuration(service.uptimeSeconds)],
      ['Запущена', fmtDateTime(service.startedUtc)]
    ]));

    host.appendChild(kvBlock('Приём данных', [
      ['Отчётов агентов', fmtNumber(ingest.reportsTotal)],
      ['Выборок активности', fmtNumber(ingest.samplesTotal)],
      ['Учтено секунд', fmtNumber(ingest.secondsTotal)],
      ['Отклонено выборок', fmtNumber(ingest.rejectedTotal)],
      ['Последний отчёт', ingest.lastReportUtc ? fmtDateTime(ingest.lastReportUtc) : '—']
    ]));

    host.appendChild(kvBlock('Учёт', [
      ['Интервал опроса', (monitoring.pollIntervalSeconds || 0) + ' с'],
      ['Окно активности N', (monitoring.activityIdleSeconds || 0) + ' с'],
      ['В буфере, секунд', fmtNumber(monitoring.pendingSeconds)],
      ['Сбросов на диск', fmtNumber(monitoring.flushesTotal)],
      ['Последний сброс', monitoring.lastFlushUtc ? fmtDateTime(monitoring.lastFlushUtc) : '—'],
      ['Обслуживание базы', monitoring.lastMaintenanceUtc ? fmtDateTime(monitoring.lastMaintenanceUtc) : '—']
    ]));

    host.appendChild(kvBlock('База и правила', [
      ['Размер базы', ((Number(database.sizeBytes) || 0) / 1048576).toFixed(2) + ' МБ'],
      ['WAL', ((Number(database.walSizeBytes) || 0) / 1048576).toFixed(2) + ' МБ'],
      ['Интервалов за сутки', fmtNumber(database.intervalsToday)],
      ['Приложений в базе', fmtNumber(database.applicationsTotal)],
      ['Групп', fmtNumber(database.groupsTotal)],
      ['Правил критического времени', fmtNumber(workflow.criticalRulesTotal)],
      ['Правил реакции', fmtNumber(workflow.rulesTotal)],
      ['Скрипты разрешены', workflow.scriptActionsEnabled ? 'да' : 'нет']
    ]));

    const body = $('diag-agents-body');
    clear(body);
    const agents = diag.agents || [];
    $('diag-agents-count').textContent = agents.length ? 'агентов: ' + fmtNumber(agents.length) : '';
    agents.forEach(function (agent) {
      const row = el('tr');
      row.appendChild(el('td', 'tabular', agent.sessionId));
      row.appendChild(el('td', null, agent.domain ? agent.domain + '\\' + agent.userName : agent.userName));
      row.appendChild(el('td', 'tabular', agent.pid || '—'));
      row.appendChild(el('td', 'tabular', agent.lastReportUtc ? fmtDateTime(agent.lastReportUtc) : '—'));
      row.appendChild(el('td', 'tabular', (Number(agent.cpuPercent) || 0).toFixed(2)));
      row.appendChild(el('td', 'tabular', (Number(agent.workingSetMb) || 0).toFixed(1) + ' МБ'));
      row.appendChild(el('td', 'tabular', fmtNumber(agent.reportsTotal)));
      const errors = (agent.errors || []).join('; ');
      row.appendChild(el('td', errors ? 'text-err' : 'muted', errors || (agent.alive ? 'нет' : 'агент не отвечает')));
      body.appendChild(row);
    });
    if (!agents.length) {
      const row = el('tr');
      const cell = el('td', 'muted', 'Агенты не зарегистрированы.');
      cell.colSpan = 8;
      row.appendChild(cell);
      body.appendChild(row);
    }
  }

  async function refreshDiagnostics() {
    try {
      renderDiagnostics(await api('api/diagnostics'));
      renderHealth(await api('api/health'));
    } catch (error) {
      setMsg($('diag-msg'), 'Ошибка: ' + describeError(error), 'error');
    }
  }

  // --------------------------------------------------------------- вкладки

  function selectTab(name) {
    const tabs = document.querySelectorAll('.tab[data-tab]');
    Array.prototype.forEach.call(tabs, function (tab) {
      tab.classList.toggle('is-active', tab.getAttribute('data-tab') === name);
      tab.setAttribute('aria-selected', tab.getAttribute('data-tab') === name ? 'true' : 'false');
    });
    Array.prototype.forEach.call(document.querySelectorAll('.panel[data-panel]'), function (panel) {
      panel.hidden = panel.getAttribute('data-panel') !== name;
    });
    if (name === 'diagnostics') refreshDiagnostics();
  }

  function applyTheme(theme) {
    if (theme) document.documentElement.setAttribute('data-theme', theme);
    else document.documentElement.removeAttribute('data-theme');
    $('btn-theme').textContent = theme === 'dark' ? 'Светлая тема' : 'Тёмная тема';
  }

  // ------------------------------------------------------------------ старт

  async function refreshCore() {
    try {
      const results = await Promise.all([api(query('api/dashboard')), api(query('api/apps') + '&sort=todaySeconds')]);
      state.dashboard = results[0];
      state.apps = (results[1] && results[1].apps) || [];
      renderDashboard(state.dashboard);
      renderApps();
      setOffline(false);
      $('updated-label').textContent = 'обновлено в ' + new Date().toLocaleTimeString('ru-RU');
    } catch (error) {
      setOffline(true, describeError(error));
    }
  }

  function startAutoRefresh() {
    if (state.autoTimer) { clearInterval(state.autoTimer); state.autoTimer = null; }
    if ($('auto-refresh').checked) {
      state.autoTimer = setInterval(function () { loadAll(); }, AUTO_REFRESH_MS);
    }
  }

  function bindEvents() {
    $('date-input').value = state.date;
    updateCsvLink();

    $('date-input').addEventListener('change', function () {
      state.date = $('date-input').value || localDayKey(new Date());
      // Смена даты сбрасывает период: иначе сутки и диапазон противоречили бы друг другу.
      state.range.from = '';
      state.range.to = '';
      updateCsvLink();
      syncAddressBar();
      loadAll();
    });
    $('btn-today').addEventListener('click', function () {
      state.date = localDayKey(new Date());
      $('date-input').value = state.date;
      state.range.from = '';
      state.range.to = '';
      updateCsvLink();
      syncAddressBar();
      loadAll();
    });
    $('btn-refresh').addEventListener('click', function () { loadAll(); });
    $('offline-retry').addEventListener('click', function () { loadAll(); });
    $('auto-refresh').addEventListener('change', startAutoRefresh);

    Array.prototype.forEach.call(document.querySelectorAll('.tab[data-tab]'), function (tab) {
      tab.addEventListener('click', function () { selectTab(tab.getAttribute('data-tab')); });
    });

    $('btn-theme').addEventListener('click', function () {
      const current = document.documentElement.getAttribute('data-theme');
      const next = current === 'dark' ? 'light' : 'dark';
      localStorage.setItem(THEME_STORAGE, next);
      applyTheme(next);
    });

    $('key-input').addEventListener('change', function () {
      state.key = $('key-input').value.trim();
      if (state.key) localStorage.setItem(KEY_STORAGE, state.key);
      else localStorage.removeItem(KEY_STORAGE);
      loadAll();
    });

    $('apps-search').addEventListener('input', renderApps);
    $('apps-search-clear').addEventListener('click', function () {
      $('apps-search').value = '';
      renderApps();
    });

    $('cr-form').addEventListener('submit', submitCritical);
    $('cr-reset').addEventListener('click', resetCriticalForm);
    $('cr-target-type').addEventListener('change', renderCriticalTargets);
    $('cr-limit').addEventListener('input', function () {
      const minutes = Number($('cr-limit').value) || 0;
      $('cr-limit-hint').textContent = 'Сохраняется в секундах: ' + Math.round(minutes * 60);
    });
    $('cr-delete').addEventListener('click', async function () {
      if (!state.edit.critical) return;
      const rule = state.criticalRules.filter(function (r) { return r.id === state.edit.critical; })[0];
      if (!window.confirm('Удалить правило «' + (rule ? rule.name : state.edit.critical) + '»?')) return;
      try {
        await api('api/critical-rules/' + state.edit.critical, { method: 'DELETE' });
        resetCriticalForm();
        toast('Правило удалено', 'ok');
        await loadAll();
      } catch (error) {
        setMsg($('cr-form-msg'), 'Ошибка: ' + describeError(error), 'error');
      }
    });

    $('g-form').addEventListener('submit', submitGroup);
    $('g-reset').addEventListener('click', resetGroupForm);
    $('g-delete').addEventListener('click', async function () {
      if (!state.edit.group) return;
      const group = state.groups.filter(function (g) { return g.id === state.edit.group; })[0];
      if (!window.confirm('Удалить группу «' + (group ? group.name : state.edit.group) + '»?')) return;
      try {
        await api('api/groups/' + state.edit.group, { method: 'DELETE' });
        resetGroupForm();
        toast('Группа удалена', 'ok');
        await loadAll();
      } catch (error) {
        setMsg($('g-form-msg'), 'Ошибка: ' + describeError(error), 'error');
      }
    });

    $('wf-form').addEventListener('submit', submitWorkflow);
    $('wf-reset').addEventListener('click', resetWorkflowForm);
    $('wf-run').addEventListener('click', runWorkflowNow);
    $('wf-trigger').addEventListener('change', syncWorkflowForm);
    $('wf-action-type').addEventListener('change', syncWorkflowForm);
    $('wf-target-type').addEventListener('change', renderWorkflowTargets);
    $('wf-delete').addEventListener('click', async function () {
      if (!state.edit.workflow) return;
      const rule = state.workflowRules.filter(function (r) { return r.id === state.edit.workflow; })[0];
      if (!window.confirm('Удалить правило «' + (rule ? rule.name : state.edit.workflow) + '»?')) return;
      try {
        await api('api/workflow/rules/' + state.edit.workflow, { method: 'DELETE' });
        resetWorkflowForm();
        toast('Правило удалено', 'ok');
        await loadAll();
      } catch (error) {
        setMsg($('wf-form-msg'), 'Ошибка: ' + describeError(error), 'error');
      }
    });

    $('settings-form').addEventListener('submit', submitSettings);
    $('s-poll').addEventListener('input', updateLoadHint);
    $('s-reset').addEventListener('click', async function () {
      try {
        renderSettings(await api('api/settings'));
        setMsg($('settings-msg'), 'Форма приведена к сохранённым значениям.', 'info');
      } catch (error) {
        setMsg($('settings-msg'), 'Ошибка: ' + describeError(error), 'error');
      }
    });

    $('diag-refresh').addEventListener('click', refreshDiagnostics);
  }

  function init() {
    applyTheme(localStorage.getItem(THEME_STORAGE) || '');
    readAddressBar();
    bindEvents();
    ensurePeriodBar();
    renderPeriodBar(null);
    updateCsvLink();
    resetCriticalForm();
    resetGroupForm();
    resetWorkflowForm();
    syncWorkflowForm();
    selectTab('overview');
    loadAll();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
